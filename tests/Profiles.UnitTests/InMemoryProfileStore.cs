using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Domain;

namespace Crm.Profiles.UnitTests;

/// <summary>The store port without SQL. `Cache` simulates identity-cache entries, including stale ones.</summary>
internal sealed class InMemoryProfileStore : IProfileStore
{
    public Dictionary<Guid, Profile> Profiles { get; } = [];

    public List<UserIdentity> Identities { get; } = [];

    public List<EventRecord> Events { get; } = [];

    /// <summary>EventRecord.ProfileId is immutable in the domain (SQL re-points rows), so ownership is tracked here.</summary>
    public Dictionary<Guid, Guid> EventOwners { get; } = [];

    public Dictionary<IdentityKey, Guid> Cache { get; } = [];

    public Dictionary<IdentityKey, Guid> Remembered { get; } = [];

    public List<IdentityKey> Forgotten { get; } = [];

    public List<string> Calls { get; } = [];

    public Task BeginAsync(CancellationToken cancellationToken)
    {
        Calls.Add("begin");
        return Task.CompletedTask;
    }

    public Task<Guid?> FindProfileIdAsync(IdentityKey key, bool bypassCache, CancellationToken cancellationToken)
    {
        if (!bypassCache && Cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult<Guid?>(cached);
        }

        return Task.FromResult(Identities.FirstOrDefault(i => i.Key == key)?.ProfileId);
    }

    public Task<Profile?> LockProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        Calls.Add($"lock:{profileId:N}");
        return Task.FromResult(Profiles.GetValueOrDefault(profileId));
    }

    public Task<IReadOnlyList<UserIdentity>> GetIdentitiesAsync(Guid profileId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<UserIdentity>>(Identities.Where(i => i.ProfileId == profileId).ToList());

    public void Add(Profile profile) => Profiles[profile.Id] = profile;

    public void Add(UserIdentity identity) => Identities.Add(identity);

    public Task<bool> TryAppendEventAsync(EventRecord record, CancellationToken cancellationToken)
    {
        if (Events.Any(e => e.Id == record.Id && e.OccurredAt == record.OccurredAt))
        {
            return Task.FromResult(false);
        }

        Events.Add(record);
        EventOwners[record.Id] = record.ProfileId;
        return Task.FromResult(true);
    }

    public Task RepointEventsAsync(Guid loserId, Guid winnerId, CancellationToken cancellationToken)
    {
        foreach (var eventId in EventOwners.Where(o => o.Value == loserId).Select(o => o.Key).ToList())
        {
            EventOwners[eventId] = winnerId;
        }

        return Task.CompletedTask;
    }

    public Task<long> CountEventsAsync(Guid profileId, CancellationToken cancellationToken)
        => Task.FromResult((long)EventOwners.Count(o => o.Value == profileId));

    public void Remember(IdentityKey key, Guid profileId) => Remembered[key] = profileId;

    public void Forget(IdentityKey key)
    {
        Remembered.Remove(key);
        Forgotten.Add(key);
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        Calls.Add("commit");
        return Task.CompletedTask;
    }

    public Profile ProfileOf(IdentityKey key) => Profiles[Identities.Single(i => i.Key == key).ProfileId];
}
