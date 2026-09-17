using System.Globalization;
using System.Security.Cryptography;
using Crm.Kernel.Redis;
using Crm.Kernel.Time;
using Crm.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Crm.Tenancy.Infrastructure.Auth;

public sealed record ApiKeyIdentity(Guid TenantId, Guid KeyId, string Prefix);

public interface IApiKeyAuthenticator
{
    Task<ApiKeyIdentity?> AuthenticateAsync(string? plaintext, CancellationToken cancellationToken);

    Task EvictAsync(string prefix);
}

/// <summary>Prefix → Redis (10 min) → SQL. The cache stores the key hash, so a hit still verifies the whole key in constant time.</summary>
public sealed partial class ApiKeyAuthenticator(
    TenancyDbContext db,
    IConnectionMultiplexer redis,
    ApiKeyUsageTracker usage,
    IClock clock,
    ILogger<ApiKeyAuthenticator> logger) : IApiKeyAuthenticator
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public async Task<ApiKeyIdentity?> AuthenticateAsync(string? plaintext, CancellationToken cancellationToken)
    {
        if (!ApiKey.TryParsePrefix(plaintext, out var prefix))
        {
            return null;
        }

        var identity = await FromCacheAsync(prefix, plaintext!) ?? await FromDatabaseAsync(prefix, plaintext!, cancellationToken);
        if (identity is null)
        {
            return null;
        }

        var now = clock.UtcNow;
        if (usage.ShouldTouch(prefix, now))
        {
            // ExecuteUpdate bypasses the change tracker: no tenant is resolved yet at authentication time.
            await db.ApiKeys.IgnoreQueryFilters()
                .Where(k => k.Id == identity.KeyId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }

        return identity;
    }

    public async Task EvictAsync(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        await redis.GetDatabase().KeyDeleteAsync(RedisKeys.ApiKey(prefix));
    }

    private async Task<ApiKeyIdentity?> FromCacheAsync(string prefix, string plaintext)
    {
        RedisValue cached;
        try
        {
            cached = await redis.GetDatabase().StringGetAsync(RedisKeys.ApiKey(prefix));
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(logger, ex);
            return null;
        }

        if (cached.IsNullOrEmpty)
        {
            return null;
        }

        var parts = cached.ToString().Split(':');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out var tenantId)
            || !Guid.TryParseExact(parts[1], "N", out var keyId))
        {
            return null;
        }

        return CryptographicOperations.FixedTimeEquals(ApiKey.Hash(plaintext), Convert.FromHexString(parts[2]))
            ? new ApiKeyIdentity(tenantId, keyId, prefix)
            : null;
    }

    private async Task<ApiKeyIdentity?> FromDatabaseAsync(string prefix, string plaintext, CancellationToken cancellationToken)
    {
        var key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(k => k.Prefix == prefix, cancellationToken);
        if (key is null || key.Verify(plaintext) != ApiKeyVerification.Valid)
        {
            return null;
        }

        var value = string.Create(CultureInfo.InvariantCulture, $"{key.TenantId:N}:{key.Id:N}:{Convert.ToHexString(key.KeyHash)}");
        try
        {
            await redis.GetDatabase().StringSetAsync(RedisKeys.ApiKey(prefix), value, CacheTtl);
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(logger, ex);
        }

        return new ApiKeyIdentity(key.TenantId, key.Id, prefix);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "API key cache unavailable; falling back to SQL")]
    private static partial void LogCacheUnavailable(ILogger logger, Exception exception);
}
