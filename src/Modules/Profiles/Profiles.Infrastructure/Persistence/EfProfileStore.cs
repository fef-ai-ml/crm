using System.Data;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Domain;
using Crm.Profiles.Infrastructure.Caching;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Crm.Profiles.Infrastructure.Persistence;

/// <summary>
/// One message, one transaction (READ COMMITTED; profile rows are taken WITH (UPDLOCK, HOLDLOCK), spec §4.2).
/// Entities go through EF so the tenant interceptor stamps and guards them; the statements EF cannot express
/// (lock hints, insert-if-absent, set-based re-point) are interpolated SQL on the same connection and transaction.
/// Disposing without <see cref="CommitAsync"/> rolls back.
/// </summary>
internal sealed class EfProfileStore(ProfilesDbContext db, IIdentityCache cache, ITenantContext tenant, IClock clock) : IProfileStore, IAsyncDisposable
{
    private readonly Dictionary<IdentityKey, Guid> _pendingSets = [];
    private readonly HashSet<IdentityKey> _pendingDeletes = [];
    private IDbContextTransaction? _transaction;

    public async Task BeginAsync(CancellationToken cancellationToken)
        => _transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

    public async Task<Guid?> FindProfileIdAsync(IdentityKey key, bool bypassCache, CancellationToken cancellationToken)
    {
        // Added in this transaction and not flushed yet.
        if (db.UserIdentities.Local.FirstOrDefault(i => i.Kind == key.Kind && i.Value == key.Value) is { } local)
        {
            return local.ProfileId;
        }

        if (!bypassCache && await cache.GetAsync(tenant.TenantId, key) is { } cached)
        {
            return cached;
        }

        var found = await db.UserIdentities.AsNoTracking()
            .Where(i => i.Kind == key.Kind && i.Value == key.Value)
            .Select(i => (Guid?)i.ProfileId)
            .FirstOrDefaultAsync(cancellationToken);

        if (found is { } profileId && !_pendingDeletes.Contains(key))
        {
            _pendingSets[key] = profileId;
        }

        return found;
    }

    public async Task<Profile?> LockProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (db.Profiles.Local.FirstOrDefault(p => p.Id == profileId) is { } tracked)
        {
            return tracked; // created or already locked by this transaction
        }

        var tenantId = tenant.TenantId;
        return await db.Profiles
            .FromSqlInterpolated($"SELECT * FROM profiles.Profiles WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = {tenantId} AND Id = {profileId}")
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserIdentity>> GetIdentitiesAsync(Guid profileId, CancellationToken cancellationToken)
        => await db.UserIdentities.Where(i => i.ProfileId == profileId).ToListAsync(cancellationToken);

    public void Add(Profile profile) => db.Profiles.Add(profile);

    public void Add(UserIdentity identity) => db.UserIdentities.Add(identity);

    public async Task<bool> TryAppendEventAsync(EventRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var tenantId = tenant.TenantId;
        if (record.TenantId != tenantId)
        {
            throw new TenantMismatchException(tenantId, record.TenantId, nameof(EventRecord));
        }

        // A profile created by this message must exist before FK_Events_ProfileId is checked.
        await db.SaveChangesAsync(cancellationToken);

        var now = EventTime.TruncateToMilliseconds(clock.UtcNow);
        try
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO profiles.Events (Id, TenantId, ProfileId, Name, Properties, OccurredAt, ReceivedAt, CreatedAt, UpdatedAt)
                SELECT {record.Id}, {tenantId}, {record.ProfileId}, {record.Name}, {record.Properties}, {record.OccurredAt}, {record.ReceivedAt}, {now}, {now}
                WHERE NOT EXISTS (
                    SELECT 1 FROM profiles.Events WITH (UPDLOCK, HOLDLOCK)
                    WHERE Id = {record.Id} AND OccurredAt = {record.OccurredAt})
                """,
                cancellationToken);
            return inserted == 1;
        }
        catch (SqlException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            // Lost a race against another delivery of the same record. A unique violation ends the statement, not the transaction.
            return false;
        }
    }

    public async Task RepointEventsAsync(Guid loserId, Guid winnerId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var now = EventTime.TruncateToMilliseconds(clock.UtcNow);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE profiles.Events SET ProfileId = {winnerId}, UpdatedAt = {now} WHERE TenantId = {tenantId} AND ProfileId = {loserId}",
            cancellationToken);
    }

    public async Task<long> CountEventsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return await db.Database
            .SqlQuery<long>($"SELECT COUNT_BIG(*) AS [Value] FROM profiles.Events WHERE TenantId = {tenantId} AND ProfileId = {profileId}")
            .SingleAsync(cancellationToken);
    }

    public void Remember(IdentityKey key, Guid profileId) => _pendingSets[key] = profileId;

    public void Forget(IdentityKey key)
    {
        _pendingSets.Remove(key);
        _pendingDeletes.Add(key);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        var transaction = _transaction ?? throw new InvalidOperationException("BeginAsync was not called.");

        await db.SaveChangesAsync(cancellationToken);

        // Spec §4.2 item 3: a merge loser's cache entries go before the commit, so nobody can read them afterwards.
        await cache.RemoveAsync(tenant.TenantId, _pendingDeletes);
        await transaction.CommitAsync(cancellationToken);

        foreach (var (key, profileId) in _pendingSets)
        {
            await cache.SetAsync(tenant.TenantId, key, profileId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
        }
    }
}
