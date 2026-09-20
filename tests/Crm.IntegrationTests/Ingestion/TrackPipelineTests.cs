using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

/// <summary>API → events.raw → Worker → schema profiles → events.processed. IT-001's timeline half needs the read API (plan F).</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class TrackPipelineTests(CrmStackFixture stack) : IAsyncLifetime
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

    [Fact]
    public async Task A_tracked_event_reaches_the_profile_timeline_and_is_announced_on_events_processed()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase", properties = new { amount = 49.9 } });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();

        var row = await Eventually.GetAsync(
            async () => (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).SingleOrDefault(e => e.Id == eventId),
            "the Worker appends the event to profiles.Events");
        row.Name.Should().Be("purchase");

        var profile = await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u1");
        profile.Should().NotBeNull();
        profile!.Id.Should().Be(row.ProfileId);
        profile.ExternalId.Should().Be("u1");
        profile.EventCount.Should().Be(1);
        profile.MergedIntoId.Should().BeNull();

        var processed = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsProcessed, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30));
        processed.Should().NotBeEmpty("events.processed is at-least-once: a rebalance while the Worker starts can announce the event twice (ruling E7)");
        var record = processed[0];
        record.MessageType.Should().Be(nameof(EventTracked));
        record.Key.Should().Be($"{tenant.TenantId:N}:{profile.Id:N}");
        record.Headers[KafkaHeaders.ContentType].Should().Be(KafkaHeaders.JsonContentType);
        record.Headers.Should().ContainKey(KafkaHeaders.TraceParent);
        var payload = JsonSerializer.Deserialize<EventTracked>(record.Value, ContractJson.Options)!;
        payload.EventId.Should().Be(eventId);
        payload.ProfileId.Should().Be(profile.Id);
        payload.Profile.ExternalId.Should().Be("u1");
    }

    [Fact]
    public async Task The_worker_exports_consumer_lag()
    {
        using var worker = stack.Worker.CreateClient();

        await Eventually.TrueAsync(
            async () => (await worker.GetStringAsync(new Uri("/metrics", UriKind.Relative))).Contains("crm_consumer_lag{", StringComparison.Ordinal),
            "lag is reported every 5 s for partitions that have fetched at least once",
            TimeSpan.FromSeconds(90));
    }
}
