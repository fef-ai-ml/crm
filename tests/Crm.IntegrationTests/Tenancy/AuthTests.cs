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
