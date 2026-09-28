using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Domain.Segments;
using Crm.Segmentation.Infrastructure.Preview;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Crm.Segmentation.Infrastructure.Endpoints;

internal static class SegmentEndpoints
{
    public static void MapRead(RouteGroupBuilder secured)
    {
        var segments = secured.MapGroup("/segments");

        segments.MapGet("/", ListAsync)
            .WithName("ListSegments")
            .WithSummary("Draft and Active segments, newest first; status=Draft|Active|Archived filters to exactly one.")
            .Produces<SegmentListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // A literal path segment: never captured by the {id:guid}-constrained routes above or below.
        segments.MapPost("/preview", PreviewAsync)
            .WithName("PreviewSegment")
            .WithSummary("Counts profiles matching a rule definition without saving it; cached for 60 s, capped at a 5 s command budget.")
            .Produces<PreviewResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        segments.MapGet("/{id:guid}", GetAsync)
            .WithName("GetSegment")
            .Produces<SegmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        segments.MapGet("/{id:guid}/runs", ListRunsAsync)
            .WithName("ListSegmentRuns")
            .WithSummary("Runs for a segment, newest first (H10).")
            .Produces<SegmentRunListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        segments.MapGet("/{id:guid}/members", ListMembersAsync)
            .WithName("ListSegmentMembers")
            .WithSummary("Profiles in the segment's current run, newest ProfileId first (H13); an empty page when no run has finished yet.")
            .Produces<SegmentMemberListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        segments.MapGet("/{id:guid}/changes", ListChangesAsync)
            .WithName("ListSegmentChanges")
            .WithSummary("Membership changes for a segment, newest first; since= filters to OccurredAt >= since (IT-029), source= to Batch or Realtime (I15).")
            .Produces<SegmentChangeListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    public static void MapWrite(RouteGroupBuilder secured)
    {
        var segments = secured.MapGroup("/segments");

        segments.MapPost("/", CreateAsync)
            .WithName("CreateSegment")
            .WithSummary("Creates a Draft segment unless status is given.")
            .Produces<SegmentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        segments.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateSegment")
            .WithSummary("Replaces name, description, definition and status.")
            .Produces<SegmentResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        segments.MapDelete("/{id:guid}", ArchiveAsync)
            .WithName("ArchiveSegment")
            .WithSummary("Archives the segment (soft delete); idempotent.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        segments.MapPost("/{id:guid}/run", RunAsync)
            .WithName("RunSegment")
            .WithSummary("Queues a batch run. A Queued or Running run for the segment is returned as-is (H5).")
            .Produces<RunQueuedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<object> ListAsync(
        [FromQuery] string? status, [FromQuery] int? limit, [FromQuery] string? cursor,
        ISegmentQueries queries, CancellationToken cancellationToken)
        => (await queries.ListAsync(status, limit, cursor, cancellationToken))
            .Match<object>(page => TypedResults.Ok(page), error => error);

    private static async Task<object> GetAsync(Guid id, ISegmentQueries queries, CancellationToken cancellationToken)
        => (await queries.GetAsync(id, cancellationToken))
            .Match<object>(segment => TypedResults.Ok(segment), error => error);

    private static async Task<object> ListRunsAsync(
        Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, ISegmentQueries queries, CancellationToken cancellationToken)
        => (await queries.ListRunsAsync(id, limit, cursor, cancellationToken))
            .Match<object>(page => TypedResults.Ok(page), error => error);

    private static async Task<object> ListMembersAsync(
        Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, ISegmentQueries queries, CancellationToken cancellationToken)
        => (await queries.ListMembersAsync(id, limit, cursor, cancellationToken))
            .Match<object>(page => TypedResults.Ok(page), error => error);

    private static async Task<object> ListChangesAsync(
        Guid id, [FromQuery] string? since, [FromQuery] string? source, [FromQuery] int? limit, [FromQuery] string? cursor, ISegmentQueries queries, CancellationToken cancellationToken)
        => (await queries.ListChangesAsync(id, since, source, limit, cursor, cancellationToken))
            .Match<object>(page => TypedResults.Ok(page), error => error);

    private static async Task<object> PreviewAsync(PreviewRequest request, PreviewService preview, CancellationToken cancellationToken)
        => (await preview.PreviewAsync(request.Definition, cancellationToken))
            .Match<object>(response => TypedResults.Ok(response), error => error);

    private static async Task<object> CreateAsync(SegmentWriteRequest request, ISegmentService service, CancellationToken cancellationToken)
        => (await service.CreateAsync(request, cancellationToken))
            .Match<object>(segment => TypedResults.Created($"/v1/segments/{segment.Id}", segment), error => error);

    private static async Task<object> UpdateAsync(Guid id, SegmentWriteRequest request, ISegmentService service, CancellationToken cancellationToken)
        => (await service.UpdateAsync(id, request, cancellationToken))
            .Match<object>(segment => TypedResults.Ok(segment), error => error);

    private static async Task<object> ArchiveAsync(Guid id, ISegmentService service, CancellationToken cancellationToken)
    {
        var result = await service.ArchiveAsync(id, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        return TypedResults.NoContent();
    }

    private static async Task<object> RunAsync(Guid id, ISegmentRunQueue queue, CancellationToken cancellationToken)
        => (await queue.EnqueueAsync(id, SegmentRunTrigger.Manual, cancellationToken))
            .Match<object>(runId => TypedResults.Accepted((string?)null, new RunQueuedResponse(runId)), error => error);
}
