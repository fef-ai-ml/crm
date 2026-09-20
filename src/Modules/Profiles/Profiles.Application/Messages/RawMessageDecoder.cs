using System.Text.Json;
using Crm.Contracts;

namespace Crm.Profiles.Application.Messages;

/// <summary>
/// Bytes → contract, plus the checks that keep a bad record from reaching SQL: the header tenant must equal the
/// payload tenant (spec §2.5), and nothing may exceed what the schema can store (the API enforces the same limits,
/// but events.raw is not writable by the API alone).
/// </summary>
public static class RawMessageDecoder
{
    private const int MaxUserId = 128;
    private const int MaxAnonymousId = 256;
    private const int MaxEventName = 64;

    public static RawMessage Decode(RawEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!envelope.Headers.TryGetValue(KafkaHeaders.TenantId, out var tenantText) || !Guid.TryParse(tenantText, out var headerTenant))
        {
            throw new PoisonMessageException($"Header '{KafkaHeaders.TenantId}' is missing or not a GUID.");
        }

        if (!envelope.Headers.TryGetValue(KafkaHeaders.MessageType, out var messageType))
        {
            throw new PoisonMessageException($"Header '{KafkaHeaders.MessageType}' is missing.");
        }

        RawMessage message = messageType switch
        {
            nameof(TrackEventRaw) => new TrackMessage(Validate(Read<TrackEventRaw>(envelope))),
            nameof(IdentifyRaw) => new IdentifyMessage(Validate(Read<IdentifyRaw>(envelope))),
            _ => throw new PoisonMessageException($"Unknown message type '{messageType}'."),
        };

        return message.TenantId == headerTenant
            ? message
            : throw new PoisonMessageException($"Header tenant {headerTenant:N} does not match payload tenant {message.TenantId:N}.");
    }

    private static T Read<T>(RawEnvelope envelope)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(envelope.Value, ContractJson.Options)
                ?? throw new PoisonMessageException($"{typeof(T).Name} payload is JSON null.");
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"{typeof(T).Name} payload is not valid JSON: {ex.Message}", ex);
        }
    }

    private static TrackEventRaw Validate(TrackEventRaw raw)
    {
        RequireEnvelopeFields(raw.EventId, raw.TenantId, raw.ReceivedAt);
        Require(!string.IsNullOrWhiteSpace(raw.Event) && raw.Event.Length <= MaxEventName, "event name is missing or longer than 64 characters");
        Require(!string.IsNullOrWhiteSpace(raw.UserId) || !string.IsNullOrWhiteSpace(raw.AnonymousId), "neither userId nor anonymousId is present");
        RequireLengths(raw.UserId, raw.AnonymousId);
        RequireObjectOrAbsent(raw.Properties, "properties");
        return raw;
    }

    private static IdentifyRaw Validate(IdentifyRaw raw)
    {
        RequireEnvelopeFields(raw.EventId, raw.TenantId, raw.ReceivedAt);
        Require(!string.IsNullOrWhiteSpace(raw.UserId), "userId is missing");
        RequireLengths(raw.UserId, raw.AnonymousId);
        RequireObjectOrAbsent(raw.Traits, "traits");
        return raw;
    }

    private static void RequireEnvelopeFields(Guid eventId, Guid tenantId, DateTime receivedAt)
        => Require(eventId != Guid.Empty && tenantId != Guid.Empty && receivedAt != default, "eventId, tenantId or receivedAt is missing");

    private static void RequireLengths(string? userId, string? anonymousId)
        => Require((userId?.Length ?? 0) <= MaxUserId && (anonymousId?.Length ?? 0) <= MaxAnonymousId, "userId or anonymousId is longer than the schema allows");

    private static void RequireObjectOrAbsent(JsonElement element, string name)
        => Require(element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object, $"{name} is not a JSON object");

    private static void Require(bool condition, string problem)
    {
        if (!condition)
        {
            throw new PoisonMessageException($"Message rejected: {problem}.");
        }
    }
}
