using Crm.Profiles.Domain;

namespace Crm.Profiles.Infrastructure.Caching;

/// <summary>t:{tenantId}:id:{kind}:{value} → profile id. Rebuildable from profiles.UserIdentities; never authoritative (ADR-0006).</summary>
internal interface IIdentityCache
{
    Task<Guid?> GetAsync(Guid tenantId, IdentityKey key);

    Task SetAsync(Guid tenantId, IdentityKey key, Guid profileId);

    Task RemoveAsync(Guid tenantId, IReadOnlyCollection<IdentityKey> keys);
}
