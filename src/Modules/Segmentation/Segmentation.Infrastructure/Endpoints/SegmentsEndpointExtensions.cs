using Microsoft.AspNetCore.Routing;

namespace Crm.Segmentation.Infrastructure.Endpoints;

public static class SegmentsEndpointExtensions
{
    /// <summary>
    /// Maps GET /segments and GET /segments/{id} onto a group the HOST has already secured (AdminOrViewer
    /// policy, Segments tag): the policy names live in Tenancy, and a module may not reference another module.
    /// </summary>
    public static RouteGroupBuilder MapSegmentsReadEndpoints(this RouteGroupBuilder secured)
    {
        ArgumentNullException.ThrowIfNull(secured);
        SegmentEndpoints.MapRead(secured);
        return secured;
    }

    /// <summary>Maps POST /segments, PUT /segments/{id} and DELETE /segments/{id} onto a group the HOST has
    /// already secured (AdminOnly policy, Segments tag).</summary>
    public static RouteGroupBuilder MapSegmentsWriteEndpoints(this RouteGroupBuilder secured)
    {
        ArgumentNullException.ThrowIfNull(secured);
        SegmentEndpoints.MapWrite(secured);
        return secured;
    }
}
