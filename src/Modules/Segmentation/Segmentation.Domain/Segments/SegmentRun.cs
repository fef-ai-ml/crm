using Crm.Kernel;

namespace Crm.Segmentation.Domain.Segments;

/// <summary>segments.SegmentRuns: one batch evaluation of a segment. Entity.Id maps to column RunId (H9).</summary>
public sealed class SegmentRun : Entity
{
    private SegmentRun()
    {
    }

    public Guid SegmentId { get; private set; }

    public SegmentRunStatus Status { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? FinishedAt { get; private set; }

    /// <summary>Set on Finished.</summary>
    public int? MemberCount { get; private set; }

    /// <summary>Audit copy of the executed SQL (parameters listed as names).</summary>
    public string? CompiledSql { get; private set; }

    /// <summary>Set on Failed.</summary>
    public string? Error { get; private set; }

    public SegmentRunTrigger Trigger { get; private set; }

    public static SegmentRun Queue(Guid tenantId, Guid segmentId, SegmentRunTrigger trigger)
        => new()
        {
            TenantId = tenantId,
            SegmentId = segmentId,
            Status = SegmentRunStatus.Queued,
            Trigger = trigger,
        };
}
