using Crm.Kernel;

namespace Crm.Profiles.Domain;

/// <summary>profiles.Events: one row per track call. Id is the API's UUIDv7; (Id, OccurredAt) is the idempotency key.</summary>
public sealed class EventRecord : Entity
{
    private EventRecord()
    {
    }

    public EventRecord(Guid id, Guid tenantId, Guid profileId, string name, string propertiesJson, DateTime occurredAt, DateTime receivedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProfileId = profileId;
        Name = name;
        Properties = propertiesJson;
        ReceivedAt = EventTime.TruncateToMilliseconds(receivedAt);
        OccurredAt = EventTime.Normalize(occurredAt, receivedAt);
    }

    public Guid ProfileId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Properties { get; private set; } = ProfileAttributes.Empty;

    public DateTime OccurredAt { get; private set; }

    public DateTime ReceivedAt { get; private set; }
}
