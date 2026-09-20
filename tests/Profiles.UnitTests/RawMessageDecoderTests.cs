using System.Text;
using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class RawMessageDecoderTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    private static RawEnvelope Envelope(object payload, string messageType, Guid? headerTenant = null)
        => new(KafkaTopics.EventsRaw, 3, 42, "key", JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options), new Dictionary<string, string>
        {
            [KafkaHeaders.TenantId] = (headerTenant ?? Tenant).ToString("N"),
            [KafkaHeaders.MessageType] = messageType,
        });

    private static TrackEventRaw Track(string? userId = "u1", string? anonymousId = null, string name = "purchase")
        => new(Guid.CreateVersion7(), Tenant, userId, anonymousId, name, JsonDocument.Parse("""{"amount":49.9}""").RootElement.Clone(), Now, Now);

    [Fact]
    public void A_track_record_decodes_to_a_TrackMessage()
    {
        var decoded = RawMessageDecoder.Decode(Envelope(Track(), nameof(TrackEventRaw)));

        decoded.Should().BeOfType<TrackMessage>().Which.Payload.Event.Should().Be("purchase");
        decoded.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public void An_identify_record_decodes_to_an_IdentifyMessage()
    {
        var payload = new IdentifyRaw(Guid.CreateVersion7(), Tenant, "u1", "a1", JsonDocument.Parse("""{"plan":"pro"}""").RootElement.Clone(), Now);

        RawMessageDecoder.Decode(Envelope(payload, nameof(IdentifyRaw))).Should().BeOfType<IdentifyMessage>().Which.Payload.AnonymousId.Should().Be("a1");
    }

    [Fact]
    public void Header_and_payload_tenant_must_agree()
    {
        var act = () => RawMessageDecoder.Decode(Envelope(Track(), nameof(TrackEventRaw), headerTenant: Guid.CreateVersion7()));

        act.Should().Throw<PoisonMessageException>().WithMessage("*tenant*");
    }

    [Fact]
    public void Malformed_json_is_poison()
    {
        var envelope = Envelope(Track(), nameof(TrackEventRaw)) with { Value = Encoding.UTF8.GetBytes("""{"eventId":"x","properties":{"a":""") };

        var act = () => RawMessageDecoder.Decode(envelope);

        act.Should().Throw<PoisonMessageException>().WithInnerException<JsonException>();
    }

    [Fact]
    public void Missing_headers_and_unknown_types_are_poison()
    {
        var noHeaders = Envelope(Track(), nameof(TrackEventRaw)) with { Headers = new Dictionary<string, string>() };
        var unknownType = Envelope(Track(), "SomethingElse");

        FluentActions.Invoking(() => RawMessageDecoder.Decode(noHeaders)).Should().Throw<PoisonMessageException>();
        FluentActions.Invoking(() => RawMessageDecoder.Decode(unknownType)).Should().Throw<PoisonMessageException>().WithMessage("*SomethingElse*");
    }

    [Fact]
    public void A_track_without_any_identifier_or_without_a_name_is_poison()
    {
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(userId: null, anonymousId: " "), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(name: ""), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
    }

    [Fact]
    public void Values_that_cannot_be_stored_are_poison()
    {
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(userId: new string('u', 129)), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(userId: null, anonymousId: new string('a', 257)), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(name: new string('e', 65)), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
    }

    [Theory]
    [InlineData("bad name!")]
    [InlineData("purchase\n")]
    public void An_event_name_outside_the_allowed_charset_is_poison(string name)
    {
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(name: name), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
    }

    [Fact]
    public void An_event_name_matching_the_data_dictionary_charset_decodes()
    {
        var decoded = RawMessageDecoder.Decode(Envelope(Track(name: "Order_Completed_2"), nameof(TrackEventRaw)));

        decoded.Should().BeOfType<TrackMessage>().Which.Payload.Event.Should().Be("Order_Completed_2");
    }

    [Fact]
    public void Properties_must_be_an_object_when_present()
    {
        var payload = Track() with { Properties = JsonDocument.Parse("[1]").RootElement.Clone() };

        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(payload, nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
    }
}
