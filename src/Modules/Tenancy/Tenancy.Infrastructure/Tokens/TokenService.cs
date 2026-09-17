using System.Text;
using Crm.Kernel.Ids;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Crm.Tenancy.Infrastructure.Tokens;

public sealed class TokenService(IOptions<AuthOptions> options, IClock clock, IIdGenerator ids) : ITokenService
{
    private readonly AuthOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public static TokenValidationParameters CreateValidationParameters(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = SigningKey(options),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = TenancyClaims.Subject,
            RoleClaimType = TenancyClaims.Role,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };
    }

    public AccessToken IssueAccessToken(string userId, Guid tenantId, IReadOnlyCollection<string> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(roles);

        var now = clock.UtcNow;
        var expires = now + _options.AccessTokenLifetime;
        var claims = new Dictionary<string, object>
        {
            [TenancyClaims.Subject] = userId,
            [TenancyClaims.TenantId] = tenantId.ToString(),
            [TenancyClaims.TokenId] = ids.NewId().ToString("N"),
            [TenancyClaims.Kind] = TenancyClaims.KindUser,
        };
        if (roles.Count == 1)
        {
            claims[TenancyClaims.Role] = roles.First();
        }
        else if (roles.Count > 1)
        {
            claims[TenancyClaims.Role] = roles.ToArray();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken(_handler.CreateToken(descriptor), (int)_options.AccessTokenLifetime.TotalSeconds, expires);
    }

    private static SymmetricSecurityKey SigningKey(AuthOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));
}
