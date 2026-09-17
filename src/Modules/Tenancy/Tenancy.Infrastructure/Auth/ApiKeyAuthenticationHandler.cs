using System.Security.Claims;
using System.Text.Encodings.Web;
using Crm.Tenancy.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Tenancy.Infrastructure.Auth;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyAuthenticator authenticator) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AuthSchemes.ApiKeyHeader, out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var identity = await authenticator.AuthenticateAsync(header.ToString(), Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("The API key is invalid or revoked.");
        }

        var claims = new[]
        {
            new Claim(TenancyClaims.TenantId, identity.TenantId.ToString()),
            new Claim(TenancyClaims.Kind, TenancyClaims.KindApiKey),
            new Claim(TenancyClaims.KeyPrefix, identity.Prefix),
            new Claim(TenancyClaims.KeyId, identity.KeyId.ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, TenancyClaims.Subject, TenancyClaims.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
