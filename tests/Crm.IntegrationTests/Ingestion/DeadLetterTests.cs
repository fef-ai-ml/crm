using System.Globalization;
using System.Net;
using System.Text;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class DeadLetterTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT005_poison_message_goes_to_the_dlq_after_three_attempts_and_the_next_event_is_processed()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        using var worker = stack.Worker.CreateClient();
        var deadLetteredBefore = await WorkerMetrics.DeadLetteredAsync(worker, KafkaTopics.EventsRaw);

        var poison = Encoding.UTF8.GetBytes($$"""{"eventId":"{{Guid.CreateVersion7()}}","tenantId":"{{tenant.TenantId}}","userId":"u-poison","event":"purchase","properties":{"a":""");
        var produced = await KafkaTestClient.ProduceAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsRaw, $"{tenant.TenantId:N}:u-poison", poison, new Dictionary<string, string>
        {
            [KafkaHeaders.TenantId] = tenant.TenantId.ToString("N"),
            [KafkaHeaders.MessageType] = nameof(TrackEventRaw),
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        });

        using var valid = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-valid", @event = "purchase" });
        valid.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var letters = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsDlq, tenant.TenantId, expected: 1, Eventually.DefaultTimeout);
        var letter = letters.Should().ContainSingle().Subject;
        letter.Value.Should().Equal(poison, "the DLQ carries the original bytes");
        letter.Key.Should().Be($"{tenant.TenantId:N}:u-poison");
        letter.Headers[KafkaHeaders.DlqError].Should().StartWith("PoisonMessageException");
        letter.Headers[KafkaHeaders.DlqStack].Should().NotBeNullOrWhiteSpace();
        letter.Headers[KafkaHeaders.DlqAttempt].Should().Be("3");
        letter.Headers[KafkaHeaders.DlqSourceTopic].Should().Be(KafkaTopics.EventsRaw);
        letter.Headers[KafkaHeaders.DlqSourcePartition].Should().Be(produced.Partition.Value.ToString(CultureInfo.InvariantCulture));
        letter.Headers[KafkaHeaders.DlqSourceOffset].Should().Be(produced.Offset.Value.ToString(CultureInfo.InvariantCulture));
        letter.Headers[KafkaHeaders.MessageType].Should().Be(nameof(TrackEventRaw), "original headers survive");

        await Eventually.TrueAsync(
            async () => await WorkerMetrics.DeadLetteredAsync(worker, KafkaTopics.EventsRaw) >= deadLetteredBefore + 1,
            "crm_dlq_total{topic=\"events.raw\"} increments");
        // Exact delta, not just "increased": crm_dlq_total is process-wide on a Worker shared by the whole run, and
        // this only holds because [Collection(StackCollectionDefinition.Name)] runs the stack's tests sequentially.
        (await WorkerMetrics.DeadLetteredAsync(worker, KafkaTopics.EventsRaw)).Should().Be(deadLetteredBefore + 1);

        await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u-valid"),
            "the Worker keeps going after a poison message (FR-07)");
        (await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u-poison")).Should().BeNull();
        KafkaTestClient.CommittedOffset(stack.KafkaBootstrapServers, KafkaTopics.ProfilesConsumerGroup, produced.TopicPartition)
            .Should().BeGreaterThan(produced.Offset.Value, "a dead-lettered record is committed, not retried forever");
    }
}
