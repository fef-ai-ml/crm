using Crm.Tenancy.Application;
using Crm.Tenancy.Infrastructure.Auth;
using Crm.Tenancy.Infrastructure.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Crm.Tenancy.Infrastructure.Endpoints;

public sealed record BootstrapTenantRequest(string? Name, string? Slug, string? AdminEmail, string? AdminPassword, string? ViewerEmail, string? ViewerPassword);

public sealed record TenantCreatedResponse(Guid TenantId, string Slug);

internal static class TenantEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        v1.MapPost("/tenants", CreateAsync)
            .AllowAnonymous()
            .WithTags(EndpointTags.Tenancy)
            .WithName("BootstrapTenant")
            .WithSummary("Development only: bootstrap a tenant with its first Admin (and optionally a Viewer).")
            .Produces<TenantCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<object> CreateAsync(BootstrapTenantRequest request, TenantBootstrapService service, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.AdminEmail))
        {
            fields["adminEmail"] = ["is required"];
        }

        if (string.IsNullOrEmpty(request.AdminPassword))
        {
            fields["adminPassword"] = ["is required"];
        }

        if (string.IsNullOrWhiteSpace(request.ViewerEmail) != string.IsNullOrEmpty(request.ViewerPassword))
        {
            fields["viewerEmail"] = ["viewerEmail and viewerPassword must be given together"];
        }

        if (fields.Count > 0)
        {
            return TenancyErrors.Validation(fields);
        }

        var result = await service.CreateAsync(
            new BootstrapTenantCommand(
                request.Name ?? string.Empty,
                request.Slug ?? string.Empty,
                request.AdminEmail!,
                request.AdminPassword!,
                string.IsNullOrWhiteSpace(request.ViewerEmail) ? null : request.ViewerEmail,
                string.IsNullOrEmpty(request.ViewerPassword) ? null : request.ViewerPassword),
            cancellationToken);

        return result.IsFailure
            ? result.Error!
            : TypedResults.Created((string?)null, new TenantCreatedResponse(result.Value.Id, result.Value.Slug));
    }
}
