using Crm.Kernel;
using Crm.Tenancy.Application;
using Crm.Tenancy.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Crm.Tenancy.Infrastructure.Endpoints;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record TokenResponse(string AccessToken, int ExpiresIn);

internal static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        var auth = v1.MapGroup("/auth").WithTags(EndpointTags.Tenancy);

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("Login")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("RefreshToken")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/logout", LogoutAsync)
            .RequireAuthorization(AuthPolicies.AdminOrViewer)
            .WithName("Logout")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task<object> LoginAsync(LoginRequest request, AuthService service, HttpContext http, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            fields["email"] = ["is required"];
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            fields["password"] = ["is required"];
        }

        if (fields.Count > 0)
        {
            return TenancyErrors.Validation(fields);
        }

        var result = await service.LoginAsync(request.Email!, request.Password!, cancellationToken);
        return Respond(result, http, environment);
    }

    private static async Task<object> RefreshAsync(AuthService service, HttpContext http, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        var result = await service.RefreshAsync(http.Request.Cookies[RefreshCookie.Name], cancellationToken);
        if (result.IsFailure)
        {
            RefreshCookie.Clear(http, environment.IsDevelopment());
        }

        return Respond(result, http, environment);
    }

    private static async Task<object> LogoutAsync(AuthService service, HttpContext http, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        var userId = http.User.FindFirst(TenancyClaims.Subject)?.Value;
        if (userId is null)
        {
            return TenancyErrors.InvalidCredentials;
        }

        await service.LogoutAsync(userId, cancellationToken);
        RefreshCookie.Clear(http, environment.IsDevelopment());
        return TypedResults.NoContent();
    }

    private static object Respond(Result<AuthSession> result, HttpContext http, IHostEnvironment environment)
    {
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var session = result.Value;
        RefreshCookie.Write(http, session.RefreshToken, session.RefreshExpiresAtUtc, environment.IsDevelopment());
        return TypedResults.Ok(new TokenResponse(session.Access.Value, session.Access.ExpiresInSeconds));
    }
}
