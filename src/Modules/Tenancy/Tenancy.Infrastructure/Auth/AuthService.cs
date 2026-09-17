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
    private static string? _dummyPasswordHash;

    /// <summary>A hash of a fixed, never-used password, computed once and reused so the timing-parity check in
    /// <see cref="LoginAsync"/> never has to hash a real value. <see cref="LazyInitializer"/> is used instead of a
    /// static <see cref="Lazy{T}"/> because computing the value needs an instance <see cref="UserManager{TUser}"/>.</summary>
    private string DummyPasswordHash => LazyInitializer.EnsureInitialized(
        ref _dummyPasswordHash,
        () => users.PasswordHasher.HashPassword(new AppUser(), "dummy-password-for-timing"));

    public async Task<Result<AuthSession>> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email);
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (user is null)
        {
            // Spend the same time as a real password check so an unknown email is not distinguishable from a
            // wrong password by timing (spec §6.4). The result is discarded: this never authenticates anyone.
            users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyPasswordHash, password);
            return TenancyErrors.InvalidCredentials;
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            return TenancyErrors.InvalidCredentials;
        }

        if (!await IsTenantActiveAsync(user.TenantId, cancellationToken) || !EnterTenant(user))
        {
            return TenancyErrors.InvalidCredentials;
        }

        return await IssueAsync(user, cancellationToken);
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

        if (!await IsTenantActiveAsync(user.TenantId, cancellationToken))
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

        return await RotateAsync(user, current, cancellationToken);
    }

    public async Task LogoutAsync(string userId, CancellationToken cancellationToken)
        => await RevokeAllAsync(userId, clock.UtcNow, cancellationToken);

    private async Task<bool> IsTenantActiveAsync(Guid tenantId, CancellationToken cancellationToken)
        => await db.Tenants.AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken);

    private bool EnterTenant(AppUser user)
    {
        if (tenant.IsResolved)
        {
            return tenant.TenantId == user.TenantId;
        }

        tenant.Resolve(user.TenantId, Guid.TryParse(user.Id, out var id) ? id : null, TenantPrincipalKind.User);
        return true;
    }

    /// <summary>Login: no prior token to rotate, so a plain insert is enough.</summary>
    private async Task<Result<AuthSession>> IssueAsync(AppUser user, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var roles = await users.GetRolesAsync(user);
        var issued = RefreshToken.Issue(user.Id, now, options.Value.RefreshTokenLifetime);
        db.RefreshTokens.Add(issued.Entity);
        await db.SaveChangesAsync(cancellationToken);

        var access = tokens.IssueAccessToken(user.Id, user.TenantId, roles.ToArray());
        return new AuthSession(access, issued.Plaintext, issued.Entity.ExpiresAt);
    }

    /// <summary>
    /// Refresh: claims the old token with a single conditional UPDATE so two concurrent refreshes of the same
    /// cookie cannot both rotate it. Whoever's UPDATE finds the row still un-revoked (claimed == 1) wins and
    /// inserts the new token in the same transaction; the loser (claimed == 0) is treated as a concurrent
    /// double-submit, not a replay, so the chain is left alone.
    /// </summary>
    private async Task<Result<AuthSession>> RotateAsync(AppUser user, RefreshToken current, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var issued = RefreshToken.Issue(user.Id, now, options.Value.RefreshTokenLifetime);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await db.RefreshTokens
            .Where(t => t.Id == current.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.ReplacedByTokenId, issued.Entity.Id)
                    .SetProperty(t => t.UpdatedAt, now),
                cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TenancyErrors.InvalidRefreshToken;
        }

        db.RefreshTokens.Add(issued.Entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var roles = await users.GetRolesAsync(user);
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
