using Crm.Kernel;

namespace Crm.Tenancy.Application;

public static class TenancyErrors
{
    /// <summary>One message for unknown email and wrong password (spec §6.4).</summary>
    public static Error InvalidCredentials { get; } = Error.Unauthorized("auth.invalid_credentials", "Email or password is incorrect.");

    public static Error InvalidRefreshToken { get; } = Error.Unauthorized("auth.invalid_refresh_token", "The refresh token is missing, expired, or revoked.");

    public static Error SlugTaken { get; } = Error.Conflict("tenant.slug_taken", "A tenant with this slug already exists.");

    public static Error EmailTaken { get; } = Error.Conflict("user.email_taken", "A user with this email already exists.");

    public static Error ApiKeyNotFound { get; } = Error.NotFound("api_key.not_found", "No such API key.");

    public static Error Validation(IReadOnlyDictionary<string, string[]> fields)
        => Error.Validation("request.invalid", "The request is not valid.", fields);

    public static Error IdentityFailure(IEnumerable<(string Code, string Description)> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var fields = errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
        return Validation(fields);
    }
}
