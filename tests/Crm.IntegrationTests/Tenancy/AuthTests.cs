using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class AuthTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly CrmApiFactory _factory = new(stack);
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT036_login_issues_a_jwt_and_a_refresh_cookie_that_rotates_and_detects_reuse()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);

        using var login = await LoginAsync(tenant.AdminEmail, TenancySeed.Password);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, expiresIn) = await ReadTokenAsync(login);
        accessToken.Should().NotBeNullOrWhiteSpace();
        expiresIn.Should().Be(900);
        var setCookie = SetCookie(login).ToLowerInvariant();
        setCookie.Should().StartWith("crm_refresh=").And.Contain("httponly").And.Contain("secure").And.Contain("samesite=strict").And.Contain("path=/v1/auth");
        var firstCookie = CookieValue(login);

        using var probe = new HttpRequestMessage(HttpMethod.Get, "/v1/_test/read-probe");
        probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using (var probeResponse = await _client.SendAsync(probe))
        {
            probeResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the issued JWT authenticates and resolves the tenant");
        }

        using var refresh = await RefreshAsync(firstCookie);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondCookie = CookieValue(refresh);
        secondCookie.Should().NotBe(firstCookie);
        var old = await FindTokenAsync(firstCookie);
        old.RevokedAt.Should().NotBeNull();
        old.ReplacedByTokenId.Should().Be((await FindTokenAsync(secondCookie)).Id);

        using var reuse = await RefreshAsync(firstCookie);
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var afterReuse = await RefreshAsync(secondCookie);
        afterReuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "reuse revokes the whole chain for that user");
    }

    [Fact]
    public async Task Suspended_tenant_can_neither_log_in_nor_refresh()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);

        using var login = await LoginAsync(tenant.AdminEmail, TenancySeed.Password);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = CookieValue(login);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var row = await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId);
            row.Suspend();
            await db.SaveChangesAsync();
        }

        using var refresh = await RefreshAsync(cookie);
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var loginAfterSuspend = await LoginAsync(tenant.AdminEmail, TenancySeed.Password);
        loginAfterSuspend.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Strict reuse detection means a client that double-submits a refresh may lose its session: a late
    /// duplicate is indistinguishable, server-side, from a replay of a stolen token, so it is treated the
    /// same way (spec §6.4). The guarantee under test is narrower but unconditional: one refresh token
    /// never yields two descendants, however many callers race to rotate it. The web client must
    /// single-flight its refresh call rather than rely on the server to make concurrent duplicates safe.
    /// </summary>
    [Fact]
    public async Task Concurrent_refreshes_with_the_same_cookie_never_fork_the_chain()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);

        using var login = await LoginAsync(tenant.AdminEmail, TenancySeed.Password);
        var cookie = CookieValue(login);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RefreshAsync(cookie)));
        try
        {
            var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
            var unauthorizedCount = responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized);
            okCount.Should().Be(1, "the first claimant always wins the atomic rotation and its 200 is never revoked afterward");
            unauthorizedCount.Should().Be(7);

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var tokens = await db.RefreshTokens.IgnoreQueryFilters()
                .Where(t => t.UserId == tenant.AdminUserId)
                .ToListAsync();

            tokens.Should().HaveCount(2, "the login token plus exactly one descendant: the chain must never fork");
            var originalHash = RefreshToken.HashOf(cookie);
            var original = tokens.Single(t => t.TokenHash.SequenceEqual(originalHash));
            var descendant = tokens.Single(t => t.Id != original.Id);
            original.ReplacedByTokenId.Should().Be(descendant.Id);

            var liveCount = tokens.Count(t => t.RevokedAt is null);
            liveCount.Should().BeLessThanOrEqualTo(1, "0 when a straggler triggered reuse detection, 1 when every loser lost at the claim");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// The RateLimitPolicies.Auth token bucket allows 20 requests/minute per client address; 30 rapid,
    /// deliberately-wrong logins against one CrmApiFactory (one client "address") must push some into 429.
    /// </summary>
    [Fact]
    public async Task Login_is_rate_limited_per_client()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);

        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => LoginAsync(tenant.AdminEmail, "Wrong-Passw0rd!")));
        try
        {
            var tooManyRequests = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
            tooManyRequests.Should().HaveCountGreaterThanOrEqualTo(5);
            tooManyRequests.Should().OnlyContain(r => r.Headers.RetryAfter != null);
            responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.TooManyRequests || r.StatusCode == HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Login_failures_are_indistinguishable()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);

        using var wrongPassword = await LoginAsync(tenant.AdminEmail, "Wrong-Passw0rd!");
        using var unknownEmail = await LoginAsync("nobody@nowhere.test", TenancySeed.Password);

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await DetailAsync(wrongPassword)).Should().Be(await DetailAsync(unknownEmail));
        wrongPassword.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Login_rejects_an_empty_body_with_field_errors()
    {
        using var response = await _client.PostAsJsonAsync("/v1/auth/login", new { email = "", password = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").TryGetProperty("email", out _).Should().BeTrue();
        body.RootElement.GetProperty("errors").TryGetProperty("password", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_401_and_logout_revokes_the_chain()
    {
        using var noCookie = await _client.PostAsync("/v1/auth/refresh", null);
        noCookie.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        using var login = await LoginAsync(tenant.ViewerEmail, TenancySeed.Password);
        var (accessToken, _) = await ReadTokenAsync(login);
        var cookie = CookieValue(login);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var logoutResponse = await _client.SendAsync(logout);
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        SetCookie(logoutResponse).ToLowerInvariant().Should().StartWith("crm_refresh=;").And.Contain("expires=");

        using var afterLogout = await RefreshAsync(cookie);
        afterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var anonymousLogout = await _client.PostAsync("/v1/auth/logout", null);
        anonymousLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IT037_viewer_role_is_read_only()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);

        (await StatusAsync(HttpMethod.Get, "/v1/_test/read-probe", viewer)).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(HttpMethod.Post, "/v1/_test/write-probe", viewer)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(HttpMethod.Post, "/v1/api-keys", viewer)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(HttpMethod.Get, "/v1/api-keys", viewer)).Should().Be(HttpStatusCode.Forbidden);

        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        (await StatusAsync(HttpMethod.Post, "/v1/_test/write-probe", admin)).Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpStatusCode> StatusAsync(HttpMethod method, string path, string bearer)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { name = "sdk" });
        }

        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password)
        => _client.PostAsJsonAsync("/v1/auth/login", new { email, password });

    private async Task<HttpResponseMessage> RefreshAsync(string cookieValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        request.Headers.Add("Cookie", $"crm_refresh={cookieValue}");
        return await _client.SendAsync(request);
    }

    private static string SetCookie(HttpResponseMessage response)
        => response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("crm_refresh=", StringComparison.Ordinal));

    private static string CookieValue(HttpResponseMessage response)
        => Uri.UnescapeDataString(SetCookie(response).Split(';')[0]["crm_refresh=".Length..]);

    private static async Task<(string AccessToken, int ExpiresIn)> ReadTokenAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("accessToken").GetString()!, body.RootElement.GetProperty("expiresIn").GetInt32());
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("detail").GetString();
    }

    private async Task<RefreshToken> FindTokenAsync(string cookieValue)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var hash = RefreshToken.HashOf(cookieValue);
        return await db.RefreshTokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.TokenHash == hash);
    }
}
