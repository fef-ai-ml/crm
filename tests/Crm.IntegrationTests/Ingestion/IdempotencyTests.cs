using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class IdempotencyTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT004_duplicate_event_id_is_ignored()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase" });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();
        await Eventually.TrueAsync(
            async () => (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Any(e => e.Id == eventId),
            "the first delivery is processed");

        // The identical record again: same key (same partition), same bytes, same headers — what a Kafka redelivery looks like.
        var original = (await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsRaw, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30))).Single();
        var headers = original.Headers.Where(h => h.Key != KafkaHeaders.TraceParent).ToDictionary(h => h.Key, h => h.Value);
        var duplicate = await KafkaTestClient.ProduceAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsRaw, original.Key, original.Value, headers);
        duplicate.Partition.Should().Be(new Partition(original.Partition));

        await Eventually.TrueAsync(
            () => Task.FromResult(KafkaTestClient.CommittedOffset(stack.KafkaBootstrapServers, KafkaTopics.ProfilesConsumerGroup, duplicate.TopicPartition) > duplicate.Offset.Value),
            "the consumer offset advances past the duplicate");

        (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Should().ContainSingle(e => e.Id == eventId);
        var profile = await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u1");
        profile!.EventCount.Should().Be(1);

        var announced = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsProcessed, tenant.TenantId, expected: 2, TimeSpan.FromSeconds(30));
        announced.Count(r => r.MessageType == nameof(EventTracked)).Should().BeGreaterThanOrEqualTo(2, "a duplicate is announced again (ruling E7): events.processed is at-least-once");
    }
}
