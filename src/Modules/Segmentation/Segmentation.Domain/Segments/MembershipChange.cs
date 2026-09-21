using Crm.Kernel;

namespace Crm.Segmentation.Domain.Segments;

/// <summary>
/// segments.MembershipChanges: the audit trail a segment's membership timeline is read from. CreatedAt and
/// UpdatedAt are not mapped (H9) — OccurredAt is the only timestamp. Entity.Id is a UUIDv7, generated at
/// construction (H7); batch writers insert with SQL `NEWID()` instead and never construct this type.
/// </summary>
public sealed class MembershipChange : Entity
{
    private MembershipChange()
    {
    }

    public Guid SegmentId { get; private set; }

    public Guid ProfileId { get; private set; }

    public MembershipChangeKind Change { get; private set; }

    public MembershipChangeSource Source { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public static MembershipChange Record(Guid tenantId, Guid segmentId, Guid profileId, MembershipChangeKind change, MembershipChangeSource source, DateTime occurredAt)
        => new()
        {
            TenantId = tenantId,
            SegmentId = segmentId,
            ProfileId = profileId,
            Change = change,
            Source = source,
            OccurredAt = occurredAt,
        };
}
