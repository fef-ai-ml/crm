using Crm.Profiles.Application.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Crm.Profiles.Infrastructure.Endpoints;

internal static class ProfileEndpoints
{
    public static void Map(RouteGroupBuilder secured)
    {
        var profiles = secured.MapGroup("/profiles");

        profiles.MapGet("/", SearchAsync)
            .WithName("SearchProfiles")
            .WithSummary("Prefix search over external id, email and phone; all live profiles when q is absent. Sorted by lastSeenAt, newest first.")
            .Produces<ProfileListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        profiles.MapGet("/{id:guid}", GetAsync)
            .WithName("GetProfile")
            .Produces<ProfileResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        profiles.MapGet("/{id:guid}/events", TimelineAsync)
            .WithName("GetProfileTimeline")
            .WithSummary("Events of one profile, newest occurredAt first; from is inclusive, to is exclusive.")
            .Produces<TimelineResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<object> SearchAsync(
        [FromQuery(Name = "q")] string? q, [FromQuery] int? limit, [FromQuery] string? cursor,
        IProfileQueries queries, CancellationToken cancellationToken)
        => (await queries.SearchAsync(q, limit, cursor, cancellationToken))
            .Match<object>(page => TypedResults.Ok(page), error => error);

    private static async Task<object> GetAsync(Guid id, IProfileQueries queries, CancellationToken cancellationToken)
        => (await queries.GetAsync(id, cancellationToken))
            .Match<object>(profile => TypedResults.Ok(profile), error => error);

    private static async Task<object> TimelineAsync(
        Guid id,
        [FromQuery(Name = "from")] DateTimeOffset? fromTime, [FromQuery(Name = "to")] DateTimeOffset? toTime,
        [FromQuery] string? name, [FromQuery] int? limit, [FromQuery] string? cursor,
        IProfileQueries queries, CancellationToken cancellationToken)
    {
        var filter = TimelineFilter.Create(fromTime, toTime, name);
        if (filter.IsFailure)
        {
            return filter.Error!;
        }

        return (await queries.TimelineAsync(id, filter.Value, limit, cursor, cancellationToken))
            .Match<object>(page => TypedResults.Ok(page), error => error);
    }
}
