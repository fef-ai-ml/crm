using Crm.Kernel.Redis;
using Crm.Segmentation.Application.Realtime;
using Crm.Segmentation.Domain.Rules;
using StackExchange.Redis;

namespace Crm.Segmentation.Infrastructure.Realtime;

/// <summary>
/// The Redis half of real-time segmentation (spec §3.4): per-(segment, event leaf, profile) counters and the
/// per-segment membership set. Every method lets <see cref="RedisException"/>/<see cref="TimeoutException"/>
/// escape; callers decide how soft a failure is (ruling I9 for the consumer, I11–I13 for runs and reads).
/// Profile and segment ids are stored as 32 lower-case hex characters (data dictionary "Value encoding").
/// </summary>
internal sealed class RealtimeMembershipStore(IConnectionMultiplexer redis)
{
    /// <summary>Members per SADD during a rebuild (I11); the whole rebuild is still one MULTI.</summary>
    internal const int RebuildChunkSize = 1000;

    /// <summary>I3: for each distinct leaf, INCR + EXPIRE NX when <paramref name="eventName"/> matches the leaf's
    /// name and <paramref name="occurredAt"/> lies inside its window; otherwise only reads the counter. Returns the
    /// count per (name, window), 0 for a counter that does not exist. All commands go out in one pipeline.</summary>
    public async Task<IReadOnlyDictionary<(string Name, RuleWindow Window), long>> CountAsync(
        Guid tenantId, Guid segmentId, IReadOnlyList<EventNode> leaves, Guid profileId, string? eventName, DateTime occurredAt, DateTime now)
    {
        var counts = new Dictionary<(string, RuleWindow), long>();
        if (leaves.Count == 0)
        {
            return counts;
        }

        var database = redis.GetDatabase();
        var batch = database.CreateBatch();
        var pending = new List<((string, RuleWindow) Leaf, Task<long>? Incremented, Task<RedisValue>? Read)>(leaves.Count);
        var expiries = new List<Task<bool>>();
        foreach (var leaf in leaves)
        {
            var key = RealtimeCounterKey.For(tenantId, segmentId, leaf, profileId);
            if (string.Equals(leaf.Name, eventName, StringComparison.Ordinal) && occurredAt >= now - leaf.Window.Duration)
            {
                pending.Add(((leaf.Name, leaf.Window), batch.StringIncrementAsync(key.Key), null));

                // NX: the TTL is armed by the first event in the window and never re-armed by later ones (IT-024).
                expiries.Add(batch.KeyExpireAsync(key.Key, key.Ttl, ExpireWhen.HasNoExpiry));
            }
            else
            {
                pending.Add(((leaf.Name, leaf.Window), null, batch.StringGetAsync(key.Key)));
            }
        }

        batch.Execute();
        foreach (var (leaf, incremented, read) in pending)
        {
            counts[leaf] = incremented is not null
                ? await incremented
                : (await read!) is { HasValue: true } value && long.TryParse((string?)value, out var parsed) ? parsed : 0;
        }

        await Task.WhenAll(expiries);
        return counts;
    }

    /// <summary>SADD; true when the profile was not a member before (an Entered transition, ruling I5).</summary>
    public Task<bool> AddAsync(Guid tenantId, Guid segmentId, Guid profileId)
        => redis.GetDatabase().SetAddAsync(RedisKeys.SegmentMembers(tenantId, segmentId), Hex(profileId));

    /// <summary>SREM; true when the profile was a member before (an Exited transition, ruling I5).</summary>
    public Task<bool> RemoveAsync(Guid tenantId, Guid segmentId, Guid profileId)
        => redis.GetDatabase().SetRemoveAsync(RedisKeys.SegmentMembers(tenantId, segmentId), Hex(profileId));

    public Task<bool> IsMemberAsync(Guid tenantId, Guid segmentId, Guid profileId)
        => redis.GetDatabase().SetContainsAsync(RedisKeys.SegmentMembers(tenantId, segmentId), Hex(profileId));

    /// <summary>SCARD (I12): 0 when the set does not exist.</summary>
    public Task<long> CountMembersAsync(Guid tenantId, Guid segmentId)
        => redis.GetDatabase().SetLengthAsync(RedisKeys.SegmentMembers(tenantId, segmentId));

    /// <summary>I13: the set is no longer maintained, so it must not keep reporting a count.</summary>
    public Task<bool> DeleteMembersAsync(Guid tenantId, Guid segmentId)
        => redis.GetDatabase().KeyDeleteAsync(RedisKeys.SegmentMembers(tenantId, segmentId));

    /// <summary>I11: replaces the set with <paramref name="members"/> in one MULTI/EXEC — DEL, then SADD in chunks
    /// — so a concurrent SCARD or SISMEMBER sees either the old set or the new one, never a half-built one.</summary>
    public async Task ReplaceMembersAsync(Guid tenantId, Guid segmentId, IReadOnlyList<Guid> members)
    {
        var key = RedisKeys.SegmentMembers(tenantId, segmentId);
        var transaction = redis.GetDatabase().CreateTransaction();
        var commands = new List<Task> { transaction.KeyDeleteAsync(key) };
        foreach (var chunk in members.Chunk(RebuildChunkSize))
        {
            commands.Add(transaction.SetAddAsync(key, chunk.Select(id => (RedisValue)Hex(id)).ToArray()));
        }

        if (!await transaction.ExecuteAsync())
        {
            throw new RedisException($"Rebuilding {key} was not committed.");
        }

        await Task.WhenAll(commands);
    }

    private static string Hex(Guid id) => id.ToString("N");
}
