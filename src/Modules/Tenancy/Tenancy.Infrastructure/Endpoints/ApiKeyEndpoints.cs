using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Crm.Tenancy.Infrastructure.Endpoints;

public sealed record CreateApiKeyRequest(string? Name);

public sealed record ApiKeyCreatedResponse(Guid Id, string Name, string Prefix, string Key);

public sealed record ApiKeyListItem(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

public sealed record ApiKeyListResponse(IReadOnlyList<ApiKeyListItem> Items, string? NextCursor);

internal static class ApiKeyEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        var keys = v1.MapGroup("/api-keys")
            .WithTags(EndpointTags.Tenancy)
            .RequireAuthorization(AuthPolicies.AdminOnly);

        keys.MapGet("/", ListAsync).WithName("ListApiKeys").Produces<ApiKeyListResponse>();

        keys.MapPost("/", CreateAsync)
            .WithName("CreateApiKey")
            .WithSummary("The full key is returned once and cannot be retrieved again.")
            .Produces<ApiKeyCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        keys.MapDelete("/{id:guid}", RevokeAsync)
            .WithName("RevokeApiKey")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<object> ListAsync(TenancyDbContext db, CancellationToken cancellationToken)
    {
        var items = await db.ApiKeys.AsNoTracking()
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyListItem(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new ApiKeyListResponse(items, null));
    }

    private static async Task<object> CreateAsync(CreateApiKeyRequest request, TenancyDbContext db, HttpContext http, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > ApiKey.NameMaxLength)
        {
            return TenancyErrors.Validation(new Dictionary<string, string[]> { ["name"] = [$"must be 1 to {ApiKey.NameMaxLength} characters"] });
        }

        var userId = http.User.FindFirst(TenancyClaims.Subject)?.Value;
        if (userId is null)
        {
            return TenancyErrors.InvalidCredentials;
        }

        var generated = ApiKey.Generate(name, userId);
        db.ApiKeys.Add(generated.Entity);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            (string?)null,
            new ApiKeyCreatedResponse(generated.Entity.Id, generated.Entity.Name, generated.Entity.Prefix, generated.Plaintext));
    }

    private static async Task<object> RevokeAsync(Guid id, TenancyDbContext db, IApiKeyAuthenticator authenticator, IClock clock, CancellationToken cancellationToken)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (key is null)
        {
            return TenancyErrors.ApiKeyNotFound;
        }

        key.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await authenticator.EvictAsync(key.Prefix, cancellationToken);
        return TypedResults.NoContent();
    }
}
