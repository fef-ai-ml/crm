using Crm.Kernel;

namespace Crm.Segmentation.Application.Segments;

/// <summary>Read side of the module: GET /segments and GET /segments/{id}. Tenant-scoped by the DbContext filter.</summary>
public interface ISegmentQueries
{
    Task<Result<SegmentListResponse>> ListAsync(string? status, int? limit, string? cursor, CancellationToken cancellationToken);

    Task<Result<SegmentResponse>> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>GET /segments/{id}/runs, newest first (H10). Not found when the segment does not exist or belongs
    /// to another tenant, mirroring <see cref="GetAsync"/>.</summary>
    Task<Result<SegmentRunListResponse>> ListRunsAsync(Guid segmentId, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>GET /segments/{id}/members (task-8 brief, H13): profiles in the segment's current run, newest
    /// <c>ProfileId</c> first. An unresolved <c>CurrentRunId</c> (no run has ever finished) is an empty page, not
    /// a 404 — only an unknown or foreign segment id is.</summary>
    Task<Result<SegmentMemberListResponse>> ListMembersAsync(Guid segmentId, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>GET /segments/{id}/changes (task-8 brief, IT-029): membership changes newest first, optionally
    /// filtered to <c>OccurredAt >= since</c>. <paramref name="since"/> is the raw query string, parsed here so an
    /// unparsable value reports <c>segments.invalid_filter</c> rather than a framework binding error.</summary>
    Task<Result<SegmentChangeListResponse>> ListChangesAsync(Guid segmentId, string? since, int? limit, string? cursor, CancellationToken cancellationToken);
}
