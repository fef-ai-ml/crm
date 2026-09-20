using Crm.Kernel;

namespace Crm.Profiles.Domain;

/// <summary>
/// profiles.UserIdentities: primary key (TenantId, Kind, Value), so TenantId is set here rather than left to the
/// save interceptor (which still verifies it). Entity.Id is not mapped for this type.
/// </summary>
public sealed class UserIdentity : Entity
{
    private UserIdentity()
    {
    }

    public UserIdentity(Guid tenantId, IdentityKey key, Guid profileId)
    {
        TenantId = tenantId;
        Kind = key.Kind;
        Value = key.Value;
        ProfileId = profileId;
    }

    public string Kind { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public Guid ProfileId { get; private set; }

    public IdentityKey Key => new(Kind, Value);

    public void RepointTo(Guid profileId) => ProfileId = profileId;
}
