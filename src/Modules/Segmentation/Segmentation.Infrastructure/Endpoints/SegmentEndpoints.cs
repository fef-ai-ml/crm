using Crm.Segmentation.Application.Segments;
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

        segments.MapGet("/{id:guid}", GetAsync)
            .WithName("GetSegment")
            .Produces<SegmentResponse>()
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
    }

    private static async Task<object> ListAsync(
        [FromQuery] string? status, [FromQuery] int? limit, [FromQuery] string? cursor,
        ISegmentQueries queries, CancellationToken cancellationToken)
        => (await queries.ListAsync(status, limit, cursor, cancellationToken))
            .Match<object>(page => TypedResults.Ok(page), error => error);

    private static async Task<object> GetAsync(Guid id, ISegmentQueries queries, CancellationToken cancellationToken)
        => (await queries.GetAsync(id, cancellationToken))
            .Match<object>(segment => TypedResults.Ok(segment), error => error);

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
}
