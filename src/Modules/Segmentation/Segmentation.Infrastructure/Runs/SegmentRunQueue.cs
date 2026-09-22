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
    private const int MaxAttempts = 2;

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

        // B6 (parked pair, queued fix): up to two attempts. The retry exists for the narrow window where the
        // unique violation's re-read (below) finds no active run because the winner already left the active
        // state — finished, or was reaped (I-2) — between the violation and the re-read; a second attempt's own
        // insert then has a clear field. Only after a second such miss does this give up, and it gives up with a
        // Result failure, never an exception.
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Cheap path (H5): a run already Queued or Running for this segment is returned as-is. Not
            // race-free by itself — UX_SegmentRuns_TenantId_SegmentId_Active (fix round 1, Important 1) is the
            // actual guarantee; the catch below is what a genuine race between two concurrent EnqueueAsync
            // calls falls back on.
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
                return run.Id;
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sql && SqlErrors.IsUniqueViolation(sql))
            {
                // Lost the race: another EnqueueAsync call (a second concurrent POST, or the scheduler)
                // committed its own Queued row for this segment between the check above and this insert. The
                // index just proved there is exactly one active run now — read it back and hand the caller that
                // winner instead of a 500 (fix round 1, Important 1).
                db.Entry(run).State = EntityState.Detached;
                var winner = await FindActiveRunAsync(segmentId, cancellationToken);
                if (winner is { } winnerId)
                {
                    return winnerId;
                }

                // The winner already left the active state before this re-read could see it — retry the whole
                // enqueue from the top instead of throwing (last attempt falls through to the loop exit below).
            }
        }

        return SegmentErrors.EnqueueConflict;
    }

    private async Task<Guid?> FindActiveRunAsync(Guid segmentId, CancellationToken cancellationToken)
        => await db.SegmentRuns.AsNoTracking()
            .Where(r => r.SegmentId == segmentId && (r.Status == SegmentRunStatus.Queued || r.Status == SegmentRunStatus.Running))
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
