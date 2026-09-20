using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Domain;

namespace Crm.Profiles.Application.Messages;

/// <summary>events.processed payloads. Key {tenantId:N}:{profileId:N} keeps one profile's messages on one partition, in order.</summary>
public static class ProcessedMessageFactory
{
    public static ProcessedMessage EventTracked(EventRecord record, Profile profile)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(profile);
        var payload = new EventTracked(record.Id, profile.TenantId, profile.Id, record.Name, ParseObject(record.Properties), record.OccurredAt, Snapshot(profile));
        return Build(profile.TenantId, profile.Id, payload);
    }

    public static ProcessedMessage ProfileUpdated(Profile profile, IReadOnlyList<string> changedKeys, DateTime occurredAt)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Build(profile.TenantId, profile.Id, new ProfileUpdated(profile.TenantId, profile.Id, changedKeys, Snapshot(profile), occurredAt));
    }

    public static ProcessedMessage ProfileMerged(Guid tenantId, Guid winnerId, Guid loserId, DateTime occurredAt)
        => Build(tenantId, winnerId, new ProfileMerged(tenantId, winnerId, loserId, occurredAt));

    private static ProcessedMessage Build<T>(Guid tenantId, Guid profileId, T payload)
        => new($"{tenantId:N}:{profileId:N}", typeof(T).Name, tenantId, JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options));

    private static ProfileSnapshot Snapshot(Profile profile)
        => new(profile.ExternalId, profile.Email, profile.Phone, ParseObject(profile.Attributes));

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
