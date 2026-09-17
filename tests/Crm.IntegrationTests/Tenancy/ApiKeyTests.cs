using System.Net;
using Crm.IntegrationTests.Fixtures;
using Crm.Kernel.Tenancy;
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
    private const string IngestProbe = "/v1/_test/ingest-probe";
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

        (await SendAsync(HttpMethod.Post, IngestProbe, valid.Plaintext)).Should().Be(HttpStatusCode.Accepted);
        (await SendAsync(HttpMethod.Post, IngestProbe, tampered)).Should().Be(HttpStatusCode.Unauthorized, "the cache is warm now and must still verify the full key");
        (await SendAsync(HttpMethod.Post, IngestProbe, revoked.Plaintext)).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, IngestProbe, "crm_live_not-even-a-key")).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/v1/_test/read-probe", valid.Plaintext)).Should().Be(HttpStatusCode.Forbidden, "API keys may call Ingest endpoints only");
    }

    [Fact]
    public async Task IT033_last_used_is_written_once_for_ten_calls_within_a_minute()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        (await SendAsync(HttpMethod.Post, IngestProbe, key.Plaintext)).Should().Be(HttpStatusCode.Accepted);
        var first = await LastUsedAtAsync(key.Id);
        first.Should().NotBeNull();

        for (var i = 0; i < 9; i++)
        {
            (await SendAsync(HttpMethod.Post, IngestProbe, key.Plaintext)).Should().Be(HttpStatusCode.Accepted);
        }

        (await LastUsedAtAsync(key.Id)).Should().Be(first);
    }

    [Fact]
    public async Task Evicting_the_cache_makes_a_revocation_effective_immediately()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        (await SendAsync(HttpMethod.Post, IngestProbe, key.Plaintext)).Should().Be(HttpStatusCode.Accepted);

        await RevokeAsync(tenant, key.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IApiKeyAuthenticator>().EvictAsync(key.Prefix);
        }

        (await SendAsync(HttpMethod.Post, IngestProbe, key.Plaintext)).Should().Be(HttpStatusCode.Unauthorized);
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
            (await SendAsync(HttpMethod.Post, IngestProbe, key.Plaintext)).Should().Be(HttpStatusCode.Accepted, "authentication must fall back to SQL while Redis is unreachable");
            (await SendAsync(HttpMethod.Post, IngestProbe, tampered)).Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await stack.UnpauseRedisAsync();
        }
    }

    private async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string apiKey)
    {
        using var request = new HttpRequestMessage(method, path);
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
