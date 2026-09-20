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
public sealed class CommitOrderingTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT006_offset_is_committed_only_after_the_sql_transaction()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var recordKey = $"{tenant.TenantId:N}:u-crash";
        var deliveries = 0;

        // The fault: SQL has committed, then the consumer dies before it publishes or commits the offset.
        using var fault = stack.Hooks.OnSqlCommitted((envelope, _) =>
        {
            if (envelope.Key == recordKey && Interlocked.Increment(ref deliveries) == 1)
            {
                throw new InvalidOperationException("IT-006: simulated crash after the SQL commit");
            }

            return Task.CompletedTask;
        });

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-crash", @event = "purchase" });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();

        await Eventually.TrueAsync(() => Task.FromResult(Volatile.Read(ref deliveries) >= 2), "the record is redelivered because its offset was never committed");

        var raw = (await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsRaw, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30))).Single();
        var partition = new TopicPartition(KafkaTopics.EventsRaw, new Partition(raw.Partition));
        await Eventually.TrueAsync(
            () => Task.FromResult(KafkaTestClient.CommittedOffset(stack.KafkaBootstrapServers, KafkaTopics.ProfilesConsumerGroup, partition) > raw.Offset),
            "the second delivery commits the offset");

        (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Should().ContainSingle(e => e.Id == eventId, "the first delivery committed SQL; the second is a duplicate (IT-004)");
        (await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u-crash"))!.EventCount.Should().Be(1);

        var announced = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsProcessed, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30));
        announced.Should().Contain(r => r.MessageType == nameof(EventTracked), "the crash happened before the publish, so the redelivery must announce the event (ruling E7)");
    }
}
