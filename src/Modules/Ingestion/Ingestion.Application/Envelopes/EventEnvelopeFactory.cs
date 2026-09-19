using System.Text.Json;
using Crm.Contracts;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Kernel.Ids;
using Crm.Kernel.Time;

namespace Crm.Ingestion.Application.Envelopes;

/// <summary>Validated request → events.raw record: assigns the event id, stamps tenant and receive time, picks the partition key.</summary>
public sealed class EventEnvelopeFactory(IIdGenerator ids, IClock clock)
{
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new Dictionary<string, string>());

    /// <summary>{tenant:N}:{userKey}; userKey is userId when present, else anonymousId, so one person's events share a partition.</summary>
    public static string PartitionKey(Guid tenantId, string? userId, string? anonymousId)
        => $"{tenantId:N}:{(IngestRules.IsBlank(userId) ? anonymousId : userId)}";

    public RawMessage Track(Guid tenantId, TrackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var payload = new TrackEventRaw(
            ids.NewId(now),
            tenantId,
            NullIfBlank(request.UserId),
            NullIfBlank(request.AnonymousId),
            request.Event!,
            ObjectOrEmpty(request.Properties),
            request.Timestamp?.UtcDateTime ?? now,
            now);
        return Build(payload.EventId, nameof(TrackEventRaw), tenantId, PartitionKey(tenantId, payload.UserId, payload.AnonymousId), payload);
    }

    public RawMessage Identify(Guid tenantId, IdentifyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var payload = new IdentifyRaw(
            ids.NewId(now),
            tenantId,
            request.UserId!,
            NullIfBlank(request.AnonymousId),
            ObjectOrEmpty(request.Traits),
            now);
        return Build(payload.EventId, nameof(IdentifyRaw), tenantId, PartitionKey(tenantId, payload.UserId, payload.AnonymousId), payload);
    }

    private static RawMessage Build<T>(Guid eventId, string messageType, Guid tenantId, string key, T payload)
        => new(eventId, messageType, key, JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KafkaHeaders.TenantId] = tenantId.ToString("N"),
            [KafkaHeaders.MessageType] = messageType,
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        });

    private static string? NullIfBlank(string? value) => IngestRules.IsBlank(value) ? null : value;

    private static JsonElement ObjectOrEmpty(JsonElement? element)
        => element is { ValueKind: JsonValueKind.Object } value ? value : EmptyObject;
}
