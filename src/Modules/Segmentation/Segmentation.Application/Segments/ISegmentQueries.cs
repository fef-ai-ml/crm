using Crm.Kernel;

namespace Crm.Segmentation.Application.Segments;

/// <summary>Read side of the module: GET /segments and GET /segments/{id}. Tenant-scoped by the DbContext filter.</summary>
public interface ISegmentQueries
{
    Task<Result<SegmentListResponse>> ListAsync(string? status, int? limit, string? cursor, CancellationToken cancellationToken);

    Task<Result<SegmentResponse>> GetAsync(Guid id, CancellationToken cancellationToken);
}
