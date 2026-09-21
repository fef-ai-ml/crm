using System.Diagnostics.CodeAnalysis;
using Crm.Kernel;
using Crm.Segmentation.Domain.Segments;

namespace Crm.Segmentation.Application.Runs;

/// <summary>
/// Write side of batch runs (task-7 brief, H5): POST /segments/{id}/run and the scheduler's own per-tenant call
/// share this. A Queued or Running run for the segment is returned as-is rather than duplicated; an Archived
/// segment is a conflict; an unknown or foreign segment is not found. A Draft segment is runnable.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name required by specification (task-7-brief.md); it enqueues runs, it is not a collection type.")]
public interface ISegmentRunQueue
{
    Task<Result<Guid>> EnqueueAsync(Guid segmentId, SegmentRunTrigger trigger, CancellationToken cancellationToken);
}
