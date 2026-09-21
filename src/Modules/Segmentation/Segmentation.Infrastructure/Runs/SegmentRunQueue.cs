using System.Diagnostics.CodeAnalysis;
using Crm.Kernel;
using Crm.Kernel.Tenancy;
using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;

namespace Crm.Segmentation.Infrastructure.Runs;

/// <summary>
/// <see cref="ISegmentRunQueue"/> over <see cref="SegmentationDbContext"/> (task-7 brief, H5). Runs entirely
/// within the ambient tenant scope: POST /segments/{id}/run uses the request's own tenant; <c>SegmentScheduler</c>
/// resolves a fresh scope per candidate segment before calling this, exactly like a Manual enqueue.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name required by specification (task-7-brief.md); it enqueues runs, it is not a collection type.")]
internal sealed class SegmentRunQueue(SegmentationDbContext db, ITenantContext tenant) : ISegmentRunQueue
{
    public async Task<Result<Guid>> EnqueueAsync(Guid segmentId, SegmentRunTrigger trigger, CancellationToken cancellationToken)
    {
        var segment = await db.Segments.SingleOrDefaultAsync(s => s.Id == segmentId, cancellationToken);
        if (segment is null)
        {
            return SegmentErrors.NotFound;
        }

        if (segment.Status == SegmentStatus.Archived)
        {
            return SegmentErrors.Archived;
        }

        // H5: a run already Queued or Running for this segment is returned as-is, never duplicated.
        var existing = await db.SegmentRuns.AsNoTracking()
            .Where(r => r.SegmentId == segmentId && (r.Status == SegmentRunStatus.Queued || r.Status == SegmentRunStatus.Running))
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is { } runId)
        {
            return runId;
        }

        var run = SegmentRun.Queue(tenant.TenantId, segmentId, trigger);
        db.SegmentRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return run.Id;
    }
}
