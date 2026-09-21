using Crm.Kernel;

namespace Crm.Segmentation.Domain.Segments;

/// <summary>
/// segments.SegmentRunMembers: the profile ids a run produced. Keyed by (RunId, ProfileId); Entity.Id, CreatedAt
/// and UpdatedAt are not mapped (H9) — the table has exactly TenantId, RunId, ProfileId. No FK to profiles
/// (cross-module boundary): ProfileId is validated by the run query, not by the database.
/// </summary>
public sealed class SegmentRunMember : Entity
{
    private SegmentRunMember()
    {
    }

    public SegmentRunMember(Guid tenantId, Guid runId, Guid profileId)
    {
        TenantId = tenantId;
        RunId = runId;
        ProfileId = profileId;
    }

    public Guid RunId { get; private set; }

    public Guid ProfileId { get; private set; }
}
