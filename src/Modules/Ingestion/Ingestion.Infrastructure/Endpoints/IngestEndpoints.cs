using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Requests;
using Crm.Kernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Crm.Ingestion.Infrastructure.Endpoints;

/// <summary>202 only after Kafka acknowledged the record; nothing on this path touches the database. Authorization, rate limit and tag come from the host's group.</summary>
internal static class IngestEndpoints
{
    public static void Map(RouteGroupBuilder ingest)
    {
        Describe(ingest.MapPost("/track", TrackAsync).WithName("Track").Produces<IngestAccepted>(StatusCodes.Status202Accepted));
        Describe(ingest.MapPost("/identify", IdentifyAsync).WithName("Identify").Produces<IngestAccepted>(StatusCodes.Status202Accepted));
        Describe(ingest.MapPost("/batch", BatchAsync).WithName("Batch").Produces<BatchAccepted>(StatusCodes.Status202Accepted));
    }

    private static void Describe(RouteHandlerBuilder endpoint) => endpoint
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<object> TrackAsync([FromBody] TrackRequest? request, IngestService service, ITenantContext tenant, CancellationToken cancellationToken)
        => (await service.TrackAsync(tenant.TenantId, request, cancellationToken))
            .Match<object>(accepted => TypedResults.Accepted((string?)null, accepted), error => error);

    private static async Task<object> IdentifyAsync([FromBody] IdentifyRequest? request, IngestService service, ITenantContext tenant, CancellationToken cancellationToken)
        => (await service.IdentifyAsync(tenant.TenantId, request, cancellationToken))
            .Match<object>(accepted => TypedResults.Accepted((string?)null, accepted), error => error);

    private static async Task<object> BatchAsync([FromBody] BatchRequest? request, IngestService service, ITenantContext tenant, CancellationToken cancellationToken)
        => (await service.BatchAsync(tenant.TenantId, request, cancellationToken))
            .Match<object>(accepted => TypedResults.Accepted((string?)null, accepted), error => error);
}
