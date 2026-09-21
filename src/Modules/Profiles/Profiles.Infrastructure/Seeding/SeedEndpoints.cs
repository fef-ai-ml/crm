using Crm.Kernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Crm.Profiles.Infrastructure.Seeding;

internal static class SeedEndpoints
{
    /// <summary>Development tooling, not API surface: mapped only in Development by the host and kept out of the OpenAPI document.</summary>
    public static void Map(RouteGroupBuilder adminOnly)
        => adminOnly.MapPost("/dev/seed-dataset", LoadAsync).ExcludeFromDescription();

    private static async Task<object> LoadAsync(SeedDatasetLoader loader, ITenantContext tenant, CancellationToken cancellationToken)
        => (await loader.LoadAsync(tenant.TenantId, cancellationToken))
            .Match<object>(result => TypedResults.Ok(result), error => error);
}
