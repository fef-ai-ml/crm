using Crm.Kernel.Redis;
using Crm.Profiles.Domain;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Crm.Profiles.Infrastructure.Caching;

/// <summary>Ruling E12: a Redis problem is a cache miss, never a failed message.</summary>
internal sealed class RedisIdentityCache(IConnectionMultiplexer redis, ILogger<RedisIdentityCache> logger) : IIdentityCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public async Task<Guid?> GetAsync(Guid tenantId, IdentityKey key)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(RedisKeys.IdentityCache(tenantId, key.Kind, key.Value));
            return value.HasValue && Guid.TryParseExact(value.ToString(), "N", out var profileId) ? profileId : null;
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            logger.LogWarning(ex, "Identity cache read failed; falling back to SQL");
            return null;
        }
    }

    public async Task SetAsync(Guid tenantId, IdentityKey key, Guid profileId)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(RedisKeys.IdentityCache(tenantId, key.Kind, key.Value), profileId.ToString("N"), Ttl);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            logger.LogWarning(ex, "Identity cache write failed; the next lookup reads SQL");
        }
    }

    public async Task RemoveAsync(Guid tenantId, IReadOnlyCollection<IdentityKey> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        try
        {
            var redisKeys = keys.Select(k => (RedisKey)RedisKeys.IdentityCache(tenantId, k.Kind, k.Value)).ToArray();
            await redis.GetDatabase().KeyDeleteAsync(redisKeys);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            // A stale entry that survives points at a merge loser; the applier follows MergedIntoId, so it is slow, not wrong.
            logger.LogWarning(ex, "Identity cache delete failed; stale entries expire within {Ttl}", Ttl);
        }
    }

    private static bool IsRedisFailure(Exception ex) => ex is RedisException or TimeoutException;
}
