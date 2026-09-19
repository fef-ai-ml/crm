using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.Kernel.Tenancy;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure;
using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ApiKeyTests(CrmStackFixture stack) : IAsyncLifetime
{
    private const string Ingest = "/v1/track";
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
    public async Task IT033_valid_tampered_and_revoked_keys()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var valid = await TenancySeed.AddApiKeyAsync(_factory, tenant, "valid");
        var revoked = await TenancySeed.AddApiKeyAsync(_factory, tenant, "revoked");
        await RevokeAsync(tenant, revoked.Id);
        var last = valid.Plaintext[^1];
        var tampered = valid.Plaintext[..^1] + (last == 'a' ? 'b' : 'a');

        (await SendAsync(HttpMethod.Post, Ingest, valid.Plaintext)).Should().Be(HttpStatusCode.Accepted);
        (await SendAsync(HttpMethod.Post, Ingest, tampered)).Should().Be(HttpStatusCode.Unauthorized, "the cache is warm now and must still verify the full key");
        (await SendAsync(HttpMethod.Post, Ingest, revoked.Plaintext)).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, Ingest, "crm_live_not-even-a-key")).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/v1/_test/read-probe", valid.Plaintext)).Should().Be(HttpStatusCode.Forbidden, "API keys may call Ingest endpoints only");
    }

    [Fact]
    public async Task IT033_last_used_is_written_once_for_ten_calls_within_a_minute()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        (await SendAsync(HttpMethod.Post, Ingest, key.Plaintext)).Should().Be(HttpStatusCode.Accepted);
        var first = await LastUsedAtAsync(key.Id);
        first.Should().NotBeNull();

        for (var i = 0; i < 9; i++)
        {
            (await SendAsync(HttpMethod.Post, Ingest, key.Plaintext)).Should().Be(HttpStatusCode.Accepted);
        }

        (await LastUsedAtAsync(key.Id)).Should().Be(first);
    }

    [Fact]
    public async Task Evicting_the_cache_makes_a_revocation_effective_immediately()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        (await SendAsync(HttpMethod.Post, Ingest, key.Plaintext)).Should().Be(HttpStatusCode.Accepted);

        await RevokeAsync(tenant, key.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IApiKeyAuthenticator>().EvictAsync(key.Prefix);
        }

        (await SendAsync(HttpMethod.Post, Ingest, key.Plaintext)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_key_authentication_falls_back_to_sql_while_redis_is_unavailable()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var last = key.Plaintext[^1];
        var tampered = key.Plaintext[..^1] + (last == 'a' ? 'b' : 'a');

        await stack.PauseRedisAsync();
        try
        {
            (await SendAsync(HttpMethod.Post, Ingest, key.Plaintext)).Should().Be(HttpStatusCode.Accepted, "authentication must fall back to SQL while Redis is unreachable");
            (await SendAsync(HttpMethod.Post, Ingest, tampered)).Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await stack.UnpauseRedisAsync();
        }
    }

    [Fact]
    public async Task IT034_full_key_is_shown_once_and_revocation_is_immediate()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var other = await TenancySeed.CreateTenantAsync(_factory);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);

        using var created = await SendJsonAsync(HttpMethod.Post, "/v1/api-keys", admin, new { name = "sdk" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var key = createdBody.RootElement.GetProperty("key").GetString()!;
        var id = createdBody.RootElement.GetProperty("id").GetGuid();
        key.Should().MatchRegex("^crm_live_[0-9A-Za-z]{32}$");
        createdBody.RootElement.GetProperty("prefix").GetString().Should().Be(key.Substring("crm_live_".Length, 8));

        using var list = await SendJsonAsync(HttpMethod.Get, "/v1/api-keys", admin, null);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var item = listBody.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("prefix").GetString().Should().HaveLength(8);
        item.TryGetProperty("key", out _).Should().BeFalse();
        item.TryGetProperty("keyHash", out _).Should().BeFalse();

        using (var scope = _factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().ApiKeys.IgnoreQueryFilters().AsNoTracking().SingleAsync(k => k.Id == id);
            row.KeyHash.Should().Equal(ApiKey.Hash(key));
        }

        (await SendAsync(HttpMethod.Post, Ingest, key)).Should().Be(HttpStatusCode.Accepted);

        var otherAdmin = TenancySeed.AccessToken(_factory, other.AdminUserId, other.TenantId, Roles.Admin);
        using var foreignDelete = await SendJsonAsync(HttpMethod.Delete, $"/v1/api-keys/{id}", otherAdmin, null);
        foreignDelete.StatusCode.Should().Be(HttpStatusCode.NotFound, "another tenant cannot see the key");

        using var deleted = await SendJsonAsync(HttpMethod.Delete, $"/v1/api-keys/{id}", admin, null);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Post, Ingest, key)).Should().Be(HttpStatusCode.Unauthorized, "revoke evicts the cache entry");

        using var blankName = await SendJsonAsync(HttpMethod.Post, "/v1/api-keys", admin, new { name = " " });
        blankName.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string bearer, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string apiKey)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { userId = "u-it", @event = "probe" });
        }

        request.Headers.Add(AuthSchemes.ApiKeyHeader, apiKey);
        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    private async Task RevokeAsync(SeededTenant tenant, Guid keyId)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(tenant.TenantId, null, TenantPrincipalKind.None);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var key = await db.ApiKeys.SingleAsync(k => k.Id == keyId);
        key.Revoke(DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task<DateTime?> LastUsedAtAsync(Guid keyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == keyId).Select(k => k.LastUsedAt).SingleAsync();
    }
}
