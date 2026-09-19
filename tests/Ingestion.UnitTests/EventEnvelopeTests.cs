using System.Text.Json;
using Crm.Contracts;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Kernel.Ids;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

public class EventEnvelopeTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void UT007_event_ids_are_uuid_v7_and_time_ordered()
    {
        var clock = new FakeClock(Now);
        var factory = new EventEnvelopeFactory(new UuidV7IdGenerator(), clock);

        var first = factory.Track(Tenant, new TrackRequest("u1", null, "purchase", null, null));
        clock.UtcNow = Now.AddMilliseconds(1);
        var second = factory.Track(Tenant, new TrackRequest("u1", null, "purchase", null, null));

        first.EventId.Version.Should().Be(7);
        second.EventId.Version.Should().Be(7);
        second.EventId.CompareTo(first.EventId).Should().BePositive();
        string.CompareOrdinal(second.EventId.ToString(), first.EventId.ToString()).Should().BePositive();
    }

    [Fact]
    public void UT008_kafka_key_is_tenant_plus_user_key()
    {
        EventEnvelopeFactory.PartitionKey(Tenant, "u1", "a1").Should().Be("11111111222233334444555555555555:u1");
        EventEnvelopeFactory.PartitionKey(Tenant, null, "a1").Should().Be("11111111222233334444555555555555:a1");
        EventEnvelopeFactory.PartitionKey(Tenant, "  ", "a1").Should().Be("11111111222233334444555555555555:a1");
    }

    [Fact]
    public void Track_payload_is_the_contract_message_with_defaults_applied()
    {
        var factory = new EventEnvelopeFactory(new UuidV7IdGenerator(), new FakeClock(Now));

        var message = factory.Track(Tenant, new TrackRequest("u1", " ", "purchase", null, null));
        var payload = JsonSerializer.Deserialize<TrackEventRaw>(message.Value, ContractJson.Options)!;

        message.MessageType.Should().Be(nameof(TrackEventRaw));
        message.Key.Should().Be("11111111222233334444555555555555:u1");
        payload.EventId.Should().Be(message.EventId);
        payload.TenantId.Should().Be(Tenant);
        payload.UserId.Should().Be("u1");
        payload.AnonymousId.Should().BeNull("blank identifiers count as absent");
        payload.Event.Should().Be("purchase");
        payload.Properties.ValueKind.Should().Be(JsonValueKind.Object);
        payload.Properties.EnumerateObject().Should().BeEmpty("absent properties become {}");
        payload.OccurredAt.Should().Be(Now, "an absent timestamp resolves to the clock (UT-004)");
        payload.ReceivedAt.Should().Be(Now);
    }

    [Fact]
    public void A_client_timestamp_becomes_OccurredAt_in_utc()
    {
        var factory = new EventEnvelopeFactory(new UuidV7IdGenerator(), new FakeClock(Now));
        var clientTime = new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.FromHours(7));

        var message = factory.Track(Tenant, new TrackRequest("u1", null, "purchase", null, clientTime));
        var payload = JsonSerializer.Deserialize<TrackEventRaw>(message.Value, ContractJson.Options)!;

        payload.OccurredAt.Should().Be(new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc));
        payload.ReceivedAt.Should().Be(Now);
    }

    [Fact]
    public void Identify_payload_and_headers()
    {
        var factory = new EventEnvelopeFactory(new UuidV7IdGenerator(), new FakeClock(Now));
        using var traits = JsonDocument.Parse("{\"plan\":\"pro\"}");

        var message = factory.Identify(Tenant, new IdentifyRequest("u2", "a9", traits.RootElement.Clone()));
        var payload = JsonSerializer.Deserialize<IdentifyRaw>(message.Value, ContractJson.Options)!;

        message.MessageType.Should().Be(nameof(IdentifyRaw));
        message.Key.Should().Be("11111111222233334444555555555555:u2");
        payload.UserId.Should().Be("u2");
        payload.AnonymousId.Should().Be("a9");
        payload.Traits.GetProperty("plan").GetString().Should().Be("pro");
        message.Headers.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [KafkaHeaders.TenantId] = "11111111222233334444555555555555",
            [KafkaHeaders.MessageType] = nameof(IdentifyRaw),
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        });
    }
}
