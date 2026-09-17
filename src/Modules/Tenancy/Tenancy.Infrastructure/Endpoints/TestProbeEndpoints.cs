using Crm.Tenancy.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Crm.Tenancy.Infrastructure.Endpoints;

/// <summary>Testing-only stand-ins for endpoints of later plans (plan ruling P4). Deleted when /v1/track and /v1/segments exist.</summary>
internal static class TestProbeEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        var probes = v1.MapGroup("/_test").ExcludeFromDescription();

        probes.MapGet("/read-probe", async (TenancyDbContext db, CancellationToken ct) =>
                TypedResults.Ok(new ProbeCount(await db.ApiKeys.CountAsync(ct))))
            .RequireAuthorization(AuthPolicies.AdminOrViewer);

        probes.MapPost("/write-probe", () => TypedResults.Ok())
            .RequireAuthorization(AuthPolicies.AdminOnly);
    }

    internal sealed record ProbeCount(int Count);
}
