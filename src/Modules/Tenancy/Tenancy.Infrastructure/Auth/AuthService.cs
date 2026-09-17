using Crm.Kernel;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crm.Tenancy.Infrastructure.Auth;

public sealed record AuthSession(AccessToken Access, string RefreshToken, DateTime RefreshExpiresAtUtc);

/// <summary>Anonymous flows start without a tenant: the user or token row is found with filters off, then the scope is resolved to that row's tenant before anything is written.</summary>
public sealed class AuthService(
    TenancyDbContext db,
    UserManager<AppUser> users,
    ITokenService tokens,
    TenantContext tenant,
    IOptions<AuthOptions> options,
    IClock clock)
{
    public async Task<Result<AuthSession>> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email);
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (user is null || !await users.CheckPasswordAsync(user, password))
        {
            return TenancyErrors.InvalidCredentials;
        }

        var active = await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.Status == TenantStatus.Active, cancellationToken);
        if (!active || !EnterTenant(user))
        {
            return TenancyErrors.InvalidCredentials;
        }

        return await IssueAsync(user, replaces: null, cancellationToken);
    }

    public async Task<Result<AuthSession>> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return TenancyErrors.InvalidRefreshToken;
        }

        var hash = RefreshToken.HashOf(refreshToken);
        var current = await db.RefreshTokens.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (current is null)
        {
            return TenancyErrors.InvalidRefreshToken;
        }

        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Id == current.UserId, cancellationToken);
        if (user is null || !EnterTenant(user))
        {
            return TenancyErrors.InvalidRefreshToken;
        }

        var now = clock.UtcNow;
        if (!current.IsActive(now))
        {
            if (current.RevokedAt is not null)
            {
                // A revoked token came back: someone else holds a copy. Kill every live token of this user.
                await RevokeAllAsync(user.Id, now, cancellationToken);
            }

            return TenancyErrors.InvalidRefreshToken;
        }

        return await IssueAsync(user, replaces: current, cancellationToken);
    }

    public async Task LogoutAsync(string userId, CancellationToken cancellationToken)
        => await RevokeAllAsync(userId, clock.UtcNow, cancellationToken);

    private bool EnterTenant(AppUser user)
    {
        if (tenant.IsResolved)
        {
            return tenant.TenantId == user.TenantId;
        }

        tenant.Resolve(user.TenantId, Guid.TryParse(user.Id, out var id) ? id : null, TenantPrincipalKind.User);
        return true;
    }

    private async Task<Result<AuthSession>> IssueAsync(AppUser user, RefreshToken? replaces, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var roles = await users.GetRolesAsync(user);
        var issued = RefreshToken.Issue(user.Id, now, options.Value.RefreshTokenLifetime);
        db.RefreshTokens.Add(issued.Entity);
        replaces?.Revoke(now, issued.Entity.Id);
        await db.SaveChangesAsync(cancellationToken);

        var access = tokens.IssueAccessToken(user.Id, user.TenantId, roles.ToArray());
        return new AuthSession(access, issued.Plaintext, issued.Entity.ExpiresAt);
    }

    private async Task RevokeAllAsync(string userId, DateTime now, CancellationToken cancellationToken)
    {
        var live = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in live)
        {
            token.Revoke(now);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
