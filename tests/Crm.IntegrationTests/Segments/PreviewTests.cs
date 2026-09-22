using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.Kernel.Redis;
using Crm.Segmentation.Application;
using Crm.Segmentation.Domain.Rules;
using Crm.Tenancy.Domain;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>IT-014 and IT-015. This class does not depend on the Kafka pipeline: it seeds through
/// `SeedDataset.LoadAsync`, which writes profile rows directly (bypassing ingestion), so it is safe to run in any
/// position relative to RateLimitTests' undrained `events.raw` partition. `SegmentHooks` is reset in
/// <see cref="DisposeAsync"/> so a rewrite callback never leaks into another test class.</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class PreviewTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly CrmApiFactory _factory = new(stack);
    private readonly List<Guid> _tenantIds = [];
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        stack.SegmentHooks.Reset();
        foreach (var tenantId in _tenantIds)
        {
            await SeedDataset.DeleteAsync(stack.SqlConnectionString, tenantId);
        }

        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT014_preview_count_is_correct_cached_and_scoped_per_tenant()
    {
        var tenant = await CreateSeededTenantAsync();
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        var definition = RuleFixtures.LoadDefinition("seed-pro-id.json");

        var rewriteCalls = 0;
        // M-3: the hooks instance is shared with the in-process Worker (it polls every 250 ms in tests), so a
        // run it happens to drain during this test must never count toward "exactly one preview COUNT".
        using (stack.SegmentHooks.OnRewriteQuery((kind, sql) => { if (kind == SegmentQueryKind.Preview) { rewriteCalls++; } return sql; }))
        {
            using var first = await _client.PostAsync("/v1/segments/preview", admin, new { definition });
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            var firstBody = await ReadBodyAsync(first);
            firstBody.GetProperty("count").GetInt32().Should().Be(412);
            firstBody.GetProperty("timedOut").GetBoolean().Should().BeFalse();
            var firstCachedAt = firstBody.GetProperty("cachedAt").GetString();
            firstCachedAt.Should().NotBeNullOrEmpty();

            using var second = await _client.PostAsync("/v1/segments/preview", admin, new { definition });
            second.StatusCode.Should().Be(HttpStatusCode.OK);
            var secondBody = await ReadBodyAsync(second);
            secondBody.GetProperty("count").GetInt32().Should().Be(412);
            secondBody.GetProperty("cachedAt").GetString().Should().Be(firstCachedAt, "a cache hit must return the original computation's timestamp, not a fresh one");

            rewriteCalls.Should().Be(1, "the second call must be served from the 60 s Redis cache, never re-executing the compiled COUNT statement (H21)");
        }

        var otherTenant = await CreateSeededTenantAsync();
        var otherAdmin = TenancySeed.AccessToken(_factory, otherTenant.AdminUserId, otherTenant.TenantId, Roles.Admin);

        var otherTenantRewriteCalls = 0;
        using (stack.SegmentHooks.OnRewriteQuery((kind, sql) => { if (kind == SegmentQueryKind.Preview) { otherTenantRewriteCalls++; } return sql; }))
        {
            using var otherFirst = await _client.PostAsync("/v1/segments/preview", otherAdmin, new { definition });
            otherFirst.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReadBodyAsync(otherFirst)).GetProperty("count").GetInt32().Should().Be(412);
            otherTenantRewriteCalls.Should().Be(1, "an identical definition posted by a different tenant must not read the first tenant's cache entry (the key is scoped per tenant)");
        }
    }

    [Fact]
    public async Task IT015_preview_times_out_after_five_seconds_and_the_timeout_is_not_cached()
    {
        var tenant = await CreateSeededTenantAsync();
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        var definition = RuleFixtures.LoadDefinition("seed-pro-id.json");

        var stopwatch = Stopwatch.StartNew();
        // M-3: a Run-kind query drained by the shared in-process Worker must never get this six-second delay
        // prepended — it would stall a live member insert, not just this preview.
        using (stack.SegmentHooks.OnRewriteQuery((kind, sql) => kind == SegmentQueryKind.Preview ? "WAITFOR DELAY '00:00:06'; " + sql : sql))
        {
            using var response = await _client.PostAsync("/v1/segments/preview", admin, new { definition });
            stopwatch.Stop();

            response.StatusCode.Should().Be(HttpStatusCode.OK, "a timeout is reported in the body, not as an error status");
            var body = await ReadBodyAsync(response);
            body.GetProperty("count").ValueKind.Should().Be(JsonValueKind.Null);
            body.GetProperty("timedOut").GetBoolean().Should().BeTrue();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5.5), "the five-second command budget must bound the wait");
        }

        using var following = await _client.PostAsync("/v1/segments/preview", admin, new { definition });
        following.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadBodyAsync(following)).GetProperty("count").GetInt32().Should().Be(412, "a timed-out attempt must never be cached");
    }

    [Fact]
    public async Task A_corrupted_cache_entry_is_treated_as_a_miss_and_overwritten()
    {
        var tenant = await CreateSeededTenantAsync();
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        var definition = RuleFixtures.LoadDefinition("seed-pro-id.json");

        var tree = RuleParser.Parse(definition).Tree!;
        var hashHex = DefinitionHash.ToHex(DefinitionHash.Compute(tree));
        var cacheKey = RedisKeys.PreviewCount(tenant.TenantId, hashHex);

        using var redis = ConnectionMultiplexer.Connect(stack.RedisConfiguration);
        await redis.GetDatabase().StringSetAsync(cacheKey, "{not valid json", TimeSpan.FromSeconds(60));

        using var response = await _client.PostAsync("/v1/segments/preview", admin, new { definition });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "a corrupted cache entry must be a miss, never a failed preview");
        var body = await ReadBodyAsync(response);
        body.GetProperty("count").GetInt32().Should().Be(412);
        body.GetProperty("timedOut").GetBoolean().Should().BeFalse();

        var overwritten = await redis.GetDatabase().StringGetAsync(cacheKey);
        overwritten.HasValue.Should().BeTrue();
        using var overwrittenJson = JsonDocument.Parse((string)overwritten!);
        overwrittenJson.RootElement.GetProperty("count").GetInt32().Should().Be(412, "the corrupted entry must be replaced with a valid one, not left in place");
    }

    /// <summary>C11/M-9: `leaves-20.json` (20 top-level `and` children) is executed by IT-042 through
    /// `CompiledQueryExecutor` directly, but never through `/preview`'s own 5 s budget before this. A wide-but-
    /// shallow tree over the seed dataset must return a real count, not time out.</summary>
    [Fact]
    public async Task Preview_of_the_widest_fixture_returns_a_count_not_a_timeout()
    {
        var tenant = await CreateSeededTenantAsync();
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        var definition = RuleFixtures.LoadDefinition("leaves-20.json");

        using var response = await _client.PostAsync("/v1/segments/preview", admin, new { definition });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body.GetProperty("timedOut").GetBoolean().Should().BeFalse();
        body.GetProperty("count").ValueKind.Should().Be(JsonValueKind.Number);
    }

    private async Task<SeededTenant> CreateSeededTenantAsync()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        return tenant;
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

}
