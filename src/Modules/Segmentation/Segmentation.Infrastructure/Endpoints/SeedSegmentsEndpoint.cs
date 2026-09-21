using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Application.Seeding;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Domain.Segments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Crm.Segmentation.Infrastructure.Endpoints;

/// <summary>
/// Development tooling, not API surface (H22): mapped only in Development by the host, beside
/// `MapProfilesDevSeed`, and kept out of the OpenAPI document. Upserts <see cref="SeedSegments.All"/> by name
/// within the caller's tenant — an existing segment, including one a user has since edited, is left untouched —
/// and queues a run for every segment that is Active, has never finished a run, and has no run already in flight.
/// <see cref="ISegmentRunQueue.EnqueueAsync"/> itself (H5) is what makes a second concurrent call for the same
/// segment safe: a Queued or Running run is returned as-is rather than duplicated.
/// </summary>
public static class SeedSegmentsEndpoint
{
    public static RouteGroupBuilder MapSegmentationDevSeed(this RouteGroupBuilder adminOnly)
    {
        ArgumentNullException.ThrowIfNull(adminOnly);
        adminOnly.MapPost("/dev/seed-segments", UpsertAsync).ExcludeFromDescription();
        return adminOnly;
    }

    private static async Task<IResult> UpsertAsync(
        SegmentationDbContext db, ISegmentService segments, ISegmentRunQueue runs, CancellationToken cancellationToken)
    {
        var results = new List<SeedSegmentResult>(SeedSegments.All.Count);

        foreach (var entry in SeedSegments.All)
        {
            var (id, status, currentRunId) = await FindOrCreateAsync(db, segments, entry, cancellationToken);

            Guid? runId = null;
            if (status == SegmentStatus.Active && currentRunId is null)
            {
                var enqueued = await runs.EnqueueAsync(id, SegmentRunTrigger.Manual, cancellationToken);
                if (enqueued.IsSuccess)
                {
                    runId = enqueued.Value;
                }
            }

            results.Add(new SeedSegmentResult(id, entry.Name, status.ToString(), runId));
        }

        return TypedResults.Ok(results);
    }

    /// <summary>An existing segment (found by name, whatever its current status) is returned as-is — its
    /// definition and status are never overwritten, so a user's edits survive a second `make seed`. A missing one
    /// is created through the same parse -&gt; canonical -&gt; hash -&gt; classify -&gt; persist path as
    /// `POST /segments` (<see cref="ISegmentService.CreateAsync"/>).</summary>
    private static async Task<(Guid Id, SegmentStatus Status, Guid? CurrentRunId)> FindOrCreateAsync(
        SegmentationDbContext db, ISegmentService segments, SeedSegments.Entry entry, CancellationToken cancellationToken)
    {
        var existing = await db.Segments.AsNoTracking().SingleOrDefaultAsync(s => s.Name == entry.Name, cancellationToken);
        if (existing is not null)
        {
            return (existing.Id, existing.Status, existing.CurrentRunId);
        }

        var request = new SegmentWriteRequest(entry.Name, entry.Description, entry.Definition, entry.Status.ToString());
        var created = await segments.CreateAsync(request, cancellationToken);
        if (created.IsSuccess)
        {
            return (created.Value.Id, entry.Status, null);
        }

        if (created.Error!.Code != SegmentErrors.NameTakenCode)
        {
            throw new InvalidOperationException($"Seeding segment '{entry.Name}' failed: {created.Error.Code} {created.Error.Message}");
        }

        // Lost a race against a concurrent seed call that created it first (mirrors EfSegmentService/SegmentRunQueue).
        var winner = await db.Segments.AsNoTracking().SingleAsync(s => s.Name == entry.Name, cancellationToken);
        return (winner.Id, winner.Status, winner.CurrentRunId);
    }

    private sealed record SeedSegmentResult(Guid Id, string Name, string Status, Guid? RunId);
}
