using Crm.Tenancy.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Crm.Tenancy.Infrastructure.Endpoints;

public static class TenancyEndpointExtensions
{
    /// <summary>After UseAuthentication, before UseRateLimiter/UseAuthorization.</summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }

    /// <summary>Testing environment only.</summary>
    public static RouteGroupBuilder MapTenancyTestProbes(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        TestProbeEndpoints.Map(v1);
        return v1;
    }

    /// <summary>Tenancy endpoints under the host's /v1 group (the group carries the Error → problem+json filter).</summary>
    public static RouteGroupBuilder MapTenancyEndpoints(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        AuthEndpoints.Map(v1);
        return v1;
    }
}
