using System.Diagnostics.CodeAnalysis;
using Crm.Kernel;
using Crm.Kernel.Tenancy;
using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Domain.Segments;
using Crm.Segmentation.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
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

        // Cheap path (H5): a run already Queued or Running for this segment is returned as-is. Not race-free by
        // itself — UX_SegmentRuns_TenantId_SegmentId_Active (fix round 1, Important 1) is the actual guarantee;
        // the catch below is what a genuine race between two concurrent EnqueueAsync calls falls back on.
        var existing = await FindActiveRunAsync(segmentId, cancellationToken);
        if (existing is { } runId)
        {
            return runId;
        }

        var run = SegmentRun.Queue(tenant.TenantId, segmentId, trigger);
        db.SegmentRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sql && SqlErrors.IsUniqueViolation(sql))
        {
            // Lost the race: another EnqueueAsync call (a second concurrent POST, or the scheduler) committed
            // its own Queued row for this segment between the check above and this insert. The index just
            // proved there is exactly one active run now — read it back and hand the caller that winner instead
            // of a 500 (fix round 1, Important 1).
            db.Entry(run).State = EntityState.Detached;
            var winner = await FindActiveRunAsync(segmentId, cancellationToken);
            return winner
                ?? throw new InvalidOperationException($"Unique violation on segment {segmentId}'s active-run index, but no active run was found afterward.");
        }

        return run.Id;
    }

    private async Task<Guid?> FindActiveRunAsync(Guid segmentId, CancellationToken cancellationToken)
        => await db.SegmentRuns.AsNoTracking()
            .Where(r => r.SegmentId == segmentId && (r.Status == SegmentRunStatus.Queued || r.Status == SegmentRunStatus.Running))
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
