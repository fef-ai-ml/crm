using System.Text;

namespace Crm.Tenancy.Application;

/// <summary>Configuration section <c>Auth</c>. HS256 with a configured key in Development; the validation parameters are built from this object only, so RS256 can replace it later.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const int MinSigningKeyBytes = 32;

    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "crm";

    public string Audience { get; set; } = "crm-api";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 7;

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshTokenDays);

    public bool IsValid(out string reason)
    {
        if (Encoding.UTF8.GetByteCount(SigningKey) < MinSigningKeyBytes)
        {
            reason = $"Auth:SigningKey must be at least {MinSigningKeyBytes} bytes.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            reason = "Auth:Issuer and Auth:Audience are required.";
            return false;
        }

        if (AccessTokenMinutes <= 0 || RefreshTokenDays <= 0)
        {
            reason = "Auth lifetimes must be positive.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
