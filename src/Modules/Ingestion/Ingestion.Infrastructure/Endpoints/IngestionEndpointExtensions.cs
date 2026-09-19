using Microsoft.AspNetCore.Routing;

namespace Crm.Ingestion.Infrastructure.Endpoints;

public static class IngestionEndpointExtensions
{
    /// <summary>
    /// Maps /track, /identify and /batch onto a group the HOST has already secured (IngestOnly policy, `ingest` rate
    /// limit, Ingest tag): the policy names live in Tenancy, and a module may not reference another module.
    /// </summary>
    public static RouteGroupBuilder MapIngestionEndpoints(this RouteGroupBuilder ingest)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        IngestEndpoints.Map(ingest);
        return ingest;
    }
}
