using System.Net;
using System.Text.Json;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using Crm.Kernel.Redis;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class MergeTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task SendAsync(string path, string apiKey, object body)
    {
        using var response = await _client.PostWithKeyAsync(path, apiKey, body);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
    }

    private Task<ProfileRow> ProfileWithEventsAsync(Guid tenantId, string kind, string value, long events)
        => Eventually.GetAsync(
            async () => await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenantId, kind, value) is { } p && p.EventCount == events ? p : null,
            $"{kind}:{value} has {events} event(s)");

    [Fact]
    public async Task IT007_identify_merges_anonymous_into_known_profile()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = (await TenancySeed.AddApiKeyAsync(_factory, tenant)).Plaintext;

        await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        var p1 = await ProfileWithEventsAsync(tenant.TenantId, "anonymous_id", "a1", 2);
        await SendAsync("/v1/track", key, new { userId = "u1", @event = "purchase" });
        var p2 = await ProfileWithEventsAsync(tenant.TenantId, "external_id", "u1", 1);
        p2.FirstSeenAt.Should().BeAfter(p1.FirstSeenAt, "the test needs P1 to be the older profile");

        await SendAsync("/v1/identify", key, new { userId = "u1", anonymousId = "a1", traits = new { plan = "pro" } });

        var loser = await Eventually.GetAsync(
            async () => await ProfilesDb.GetAsync(stack.SqlConnectionString, tenant.TenantId, p2.Id) is { MergedIntoId: not null } merged ? merged : null,
            "P2 is marked as merged");
        loser.MergedIntoId.Should().Be(p1.Id, "the older profile wins (FR-09)");
        loser.ExternalId.Should().BeNull("a unique value has one owner");

        var winner = (await ProfilesDb.GetAsync(stack.SqlConnectionString, tenant.TenantId, p1.Id))!;
        winner.MergedIntoId.Should().BeNull();
        winner.ExternalId.Should().Be("u1");
        winner.EventCount.Should().Be(3);
        using (var attributes = JsonDocument.Parse(winner.Attributes))
        {
            attributes.RootElement.GetProperty("plan").GetString().Should().Be("pro");
        }

        (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Should().HaveCount(3).And.OnlyContain(e => e.ProfileId == p1.Id);
        (await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "anonymous_id", "a1"))!.Id.Should().Be(p1.Id);
        (await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u1"))!.Id.Should().Be(p1.Id);

        var processed = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsProcessed, tenant.TenantId, expected: 5, TimeSpan.FromSeconds(30));
        var merged = processed.Where(r => r.MessageType == nameof(ProfileMerged)).Should().NotBeEmpty().And.Subject.First();
        var payload = JsonSerializer.Deserialize<ProfileMerged>(merged.Value, ContractJson.Options)!;
        payload.WinnerProfileId.Should().Be(p1.Id);
        payload.LoserProfileId.Should().Be(p2.Id);
        merged.Key.Should().Be($"{tenant.TenantId:N}:{p1.Id:N}");
        processed.Should().Contain(r => r.MessageType == nameof(ProfileUpdated));

        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);
        using var mergedProfile = await _client.GetAsync($"/v1/profiles/{p2.Id}", viewer);
        mergedProfile.StatusCode.Should().Be(HttpStatusCode.NotFound, "a merge loser is excluded from every read (FR-09)");
        using var mergedTimeline = await _client.GetAsync($"/v1/profiles/{p2.Id}/events", viewer);
        mergedTimeline.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.ReadAsync<ProfileResponse>($"/v1/profiles/{p1.Id}", viewer)).EventCount.Should().Be(winner.EventCount);
    }

    [Fact]
    public async Task IT009_identity_cache_is_invalidated_on_merge()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = (await TenancySeed.AddApiKeyAsync(_factory, tenant)).Plaintext;
        var redis = stack.Worker.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var cacheKey = RedisKeys.IdentityCache(tenant.TenantId, "anonymous_id", "a1");

        // Here the known user is older, so the anonymous profile P2 is the loser.
        await SendAsync("/v1/track", key, new { userId = "u1", @event = "purchase" });
        var p1 = await ProfileWithEventsAsync(tenant.TenantId, "external_id", "u1", 1);
        await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        var p2 = await ProfileWithEventsAsync(tenant.TenantId, "anonymous_id", "a1", 1);
        await Eventually.TrueAsync(async () => (await redis.StringGetAsync(cacheKey)).ToString() == p2.Id.ToString("N"), "a1 is cached to P2 before the merge");

        await SendAsync("/v1/identify", key, new { userId = "u1", anonymousId = "a1" });
        await Eventually.TrueAsync(
            async () => await ProfilesDb.GetAsync(stack.SqlConnectionString, tenant.TenantId, p2.Id) is { MergedIntoId: not null },
            "the merge commits");

        // The delete happens inside the transaction, so once the merge is visible the key is already gone.
        (await redis.KeyExistsAsync(cacheKey)).Should().BeFalse();

        await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        await Eventually.TrueAsync(async () => (await redis.StringGetAsync(cacheKey)).ToString() == p1.Id.ToString("N"), "the next lookup caches the winner");
        (await ProfileWithEventsAsync(tenant.TenantId, "external_id", "u1", 3)).Id.Should().Be(p1.Id);
    }

    [Fact]
    public async Task IT010_event_count_is_recomputed_after_merge()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = (await TenancySeed.AddApiKeyAsync(_factory, tenant)).Plaintext;

        for (var i = 0; i < 2; i++)
        {
            await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        }

        var p1 = await ProfileWithEventsAsync(tenant.TenantId, "anonymous_id", "a1", 2);
        for (var i = 0; i < 3; i++)
        {
            await SendAsync("/v1/track", key, new { userId = "u1", @event = "purchase" });
        }

        await ProfileWithEventsAsync(tenant.TenantId, "external_id", "u1", 3);

        await SendAsync("/v1/identify", key, new { userId = "u1", anonymousId = "a1" });

        var winner = await Eventually.GetAsync(
            async () => await ProfilesDb.GetAsync(stack.SqlConnectionString, tenant.TenantId, p1.Id) is { EventCount: 5 } w ? w : null,
            "the winner's EventCount is COUNT(*) of its events after the merge");
        winner.ExternalId.Should().Be("u1");
    }
}
