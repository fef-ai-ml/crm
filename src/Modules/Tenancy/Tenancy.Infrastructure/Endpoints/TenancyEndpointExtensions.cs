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

    /// <summary>Tenancy endpoints under the host's /v1 group (the group carries the Error → problem+json filter).</summary>
    public static RouteGroupBuilder MapTenancyEndpoints(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        AuthEndpoints.Map(v1);
        ApiKeyEndpoints.Map(v1);
        return v1;
    }

    /// <summary>Development only (the host also maps it while exporting OpenAPI so the document does not depend on the environment).</summary>
    public static RouteGroupBuilder MapTenancyBootstrap(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        TenantEndpoints.Map(v1);
        return v1;
    }
}
