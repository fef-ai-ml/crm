using Crm.Profiles.Domain;

namespace Crm.Profiles.Application.Pipeline;

/// <summary>
/// Everything the pipeline needs from SQL and the identity cache for ONE message. Scoped: one instance, one
/// transaction, one tenant. Nothing is visible to other transactions before <see cref="CommitAsync"/>.
/// </summary>
public interface IProfileStore
{
    Task BeginAsync(CancellationToken cancellationToken);

    /// <summary>Identity cache first (unless bypassed), then profiles.UserIdentities. A database hit is remembered after the commit.</summary>
    Task<Guid?> FindProfileIdAsync(IdentityKey key, bool bypassCache, CancellationToken cancellationToken);

    /// <summary>Loads the row WITH (UPDLOCK, HOLDLOCK) and tracks it; null when the tenant has no such profile.</summary>
    Task<Profile?> LockProfileAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Tracked, so re-pointing them is saved by <see cref="CommitAsync"/>.</summary>
    Task<IReadOnlyList<UserIdentity>> GetIdentitiesAsync(Guid profileId, CancellationToken cancellationToken);

    void Add(Profile profile);

    void Add(UserIdentity identity);

    /// <summary>Inserts unless (Id, OccurredAt) already exists. False = duplicate (FR-06). Pending profile changes are flushed first so the foreign key holds.</summary>
    Task<bool> TryAppendEventAsync(EventRecord record, CancellationToken cancellationToken);

    Task RepointEventsAsync(Guid loserId, Guid winnerId, CancellationToken cancellationToken);

    Task<long> CountEventsAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Cache write, applied after the commit.</summary>
    void Remember(IdentityKey key, Guid profileId);

    /// <summary>Cache delete, applied inside the transaction before the commit; cancels a pending write for the key.</summary>
    void Forget(IdentityKey key);

    Task CommitAsync(CancellationToken cancellationToken);
}
