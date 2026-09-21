using Crm.Kernel;

namespace Crm.Segmentation.Application.Segments;

/// <summary>Write side of the module: POST /segments, PUT /segments/{id}, DELETE /segments/{id} (archive).
/// Every method validates and persists (parse → canonical → hash → classify → save, H15/H20).</summary>
public interface ISegmentService
{
    Task<Result<SegmentResponse>> CreateAsync(SegmentWriteRequest request, CancellationToken cancellationToken);

    Task<Result<SegmentResponse>> UpdateAsync(Guid id, SegmentWriteRequest request, CancellationToken cancellationToken);

    /// <summary>Archives the segment (soft delete). Idempotent: archiving an already-archived segment still
    /// succeeds; an unknown id fails with <see cref="SegmentErrors.NotFound"/>.</summary>
    Task<Result> ArchiveAsync(Guid id, CancellationToken cancellationToken);
}
