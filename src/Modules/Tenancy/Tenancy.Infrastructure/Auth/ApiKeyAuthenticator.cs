using System.Data.Common;
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

        var (hit, cachedIdentity) = await FromCacheAsync(prefix, plaintext!);
        var identity = hit ? cachedIdentity : await FromDatabaseAsync(prefix, plaintext!, cancellationToken);
        if (identity is null)
        {
            return null;
        }

        var now = clock.UtcNow;
        if (usage.ShouldTouch(prefix, now))
        {
            // ExecuteUpdate bypasses the change tracker: no tenant is resolved yet at authentication time.
            // Best-effort: a failure to record LastUsedAt must not fail authentication.
            try
            {
                await db.ApiKeys.IgnoreQueryFilters()
                    .Where(k => k.Id == identity.KeyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
            }
            catch (Exception ex) when (ex is DbException or DbUpdateException)
            {
                LogLastUsedNotRecorded(logger, ex);
            }
        }

        return identity;
    }

    public async Task EvictAsync(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        // Unlike the cache read/write paths, a failed eviction must surface: swallowing it would let a
        // revoked key keep authenticating from a stale cache entry for up to the full 10-minute TTL.
        await redis.GetDatabase().KeyDeleteAsync(RedisKeys.ApiKey(prefix));
    }

    /// <summary>
    /// Returns Hit=true only when the cache held a parseable entry for the prefix; the caller must not fall
    /// through to SQL in that case, even when Identity is null (a parseable-but-mismatching entry is a
    /// rejection, not a miss). Hit=false covers an absent key, a Redis outage, and a corrupt cache value —
    /// all three fall through to SQL, which then overwrites a corrupt entry with a good one.
    /// </summary>
    private async Task<(bool Hit, ApiKeyIdentity? Identity)> FromCacheAsync(string prefix, string plaintext)
    {
        RedisValue cached;
        try
        {
            cached = await redis.GetDatabase().StringGetAsync(RedisKeys.ApiKey(prefix));
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            LogCacheUnavailable(logger, ex);
            return (false, null);
        }

        if (cached.IsNullOrEmpty)
        {
            return (false, null);
        }

        if (!ApiKeyCacheEntry.TryParse(cached.ToString(), out var tenantId, out var keyId, out var keyHash))
        {
            return (false, null);
        }

        return CryptographicOperations.FixedTimeEquals(ApiKey.Hash(plaintext), keyHash)
            ? (true, new ApiKeyIdentity(tenantId, keyId, prefix))
            : (true, null);
    }

    private async Task<ApiKeyIdentity?> FromDatabaseAsync(string prefix, string plaintext, CancellationToken cancellationToken)
    {
        var key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(k => k.Prefix == prefix, cancellationToken);
        if (key is null || key.Verify(plaintext) != ApiKeyVerification.Valid)
        {
            return null;
        }

        var value = ApiKeyCacheEntry.Format(key.TenantId, key.Id, key.KeyHash);
        try
        {
            await redis.GetDatabase().StringSetAsync(RedisKeys.ApiKey(prefix), value, CacheTtl);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            LogCacheUnavailable(logger, ex);
        }

        return new ApiKeyIdentity(key.TenantId, key.Id, prefix);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "API key cache unavailable; falling back to SQL")]
    private static partial void LogCacheUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record API key last use")]
    private static partial void LogLastUsedNotRecorded(ILogger logger, Exception exception);
}
