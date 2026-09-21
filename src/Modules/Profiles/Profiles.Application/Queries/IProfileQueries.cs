using Crm.Kernel;

namespace Crm.Profiles.Application.Queries;

/// <summary>Read side of the module. Every method excludes merge losers and is tenant-scoped by the DbContext filter.</summary>
public interface IProfileQueries
{
    Task<Result<ProfileListResponse>> SearchAsync(string? q, int? limit, string? cursor, CancellationToken cancellationToken);

    Task<Result<ProfileResponse>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<TimelineResponse>> TimelineAsync(Guid id, TimelineFilter filter, int? limit, string? cursor, CancellationToken cancellationToken);
}
