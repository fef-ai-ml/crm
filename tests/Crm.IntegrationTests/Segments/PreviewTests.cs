using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.Kernel.Redis;
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
        var definition = LoadDefinition("seed-pro-id.json");

        var rewriteCalls = 0;
        using (stack.SegmentHooks.OnRewriteQuery((_, sql) => { rewriteCalls++; return sql; }))
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
        using (stack.SegmentHooks.OnRewriteQuery((_, sql) => { otherTenantRewriteCalls++; return sql; }))
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
        var definition = LoadDefinition("seed-pro-id.json");

        var stopwatch = Stopwatch.StartNew();
        using (stack.SegmentHooks.OnRewriteQuery((_, sql) => "WAITFOR DELAY '00:00:06'; " + sql))
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
        var definition = LoadDefinition("seed-pro-id.json");

        var tree = RuleParser.Parse(definition).Tree!;
        var hashHex = DefinitionHash.ToHex(DefinitionHash.Compute(tree));
        var cacheKey = RedisKeys.PreviewCount(tenant.TenantId, hashHex);

        using var redis = ConnectionMultiplexer.Connect(stack.RedisConfiguration);
        await redis.GetDatabase().StringSetAsync(cacheKey, "{not valid json", TimeSpan.FromSeconds(60));

        using var response = await _client.PostAsync("/v1/segments/preview", admin, new { definition });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "a corrupted cache entry must be a miss, never a failed preview (fix1)");
        var body = await ReadBodyAsync(response);
        body.GetProperty("count").GetInt32().Should().Be(412);
        body.GetProperty("timedOut").GetBoolean().Should().BeFalse();

        var overwritten = await redis.GetDatabase().StringGetAsync(cacheKey);
        overwritten.HasValue.Should().BeTrue();
        using var overwrittenJson = JsonDocument.Parse((string)overwritten!);
        overwrittenJson.RootElement.GetProperty("count").GetInt32().Should().Be(412, "the corrupted entry must be replaced with a valid one, not left in place");
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

    /// <summary>Mirrors SegmentCrudTests.LoadDefinition: walks up from the test binary to the directory containing Crm.sln.</summary>
    private static JsonElement LoadDefinition(string fixtureFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("Crm.sln not found above " + AppContext.BaseDirectory);
        }

        var path = Path.Combine(dir.FullName, "tests", "fixtures", "rules", "valid", fixtureFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone().GetProperty("definition");
    }
}
