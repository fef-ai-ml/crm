using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>Records as the API would have produced them, for tests that call the Worker's handler without going through Kafka.</summary>
public static class RawEnvelopes
{
    public static RawEnvelope Track(Guid tenantId, string? userId, string? anonymousId, DateTime at)
        => Build(tenantId, userId ?? anonymousId!, nameof(TrackEventRaw), new TrackEventRaw(Guid.CreateVersion7(), tenantId, userId, anonymousId, "purchase", Json("{}"), at, at));

    public static RawEnvelope Identify(Guid tenantId, string userId, string? anonymousId, string traitsJson, DateTime at)
        => Build(tenantId, userId, nameof(IdentifyRaw), new IdentifyRaw(Guid.CreateVersion7(), tenantId, userId, anonymousId, Json(traitsJson), at));

    private static RawEnvelope Build<T>(Guid tenantId, string userKey, string messageType, T payload)
        => new(KafkaTopics.EventsRaw, 0, 0, $"{tenantId:N}:{userKey}", JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options), new Dictionary<string, string>
        {
            [KafkaHeaders.TenantId] = tenantId.ToString("N"),
            [KafkaHeaders.MessageType] = messageType,
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        });

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
