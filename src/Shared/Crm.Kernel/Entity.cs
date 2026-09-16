namespace Crm.Kernel;

/// <summary>Base for every tenant-scoped persistent object. Ids are UUIDv7 so they sort by creation time.</summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    public Guid TenantId { get; protected internal set; }

    public DateTime CreatedAt { get; protected internal set; }

    public DateTime UpdatedAt { get; protected internal set; }
}
