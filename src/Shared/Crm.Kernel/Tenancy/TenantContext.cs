namespace Crm.Kernel.Tenancy;

public sealed class TenantContext : ITenantContext
{
    private Guid _tenantId;

    public bool IsResolved { get; private set; }

    public Guid TenantId => IsResolved ? _tenantId : throw new TenantContextMissingException();

    public Guid? UserId { get; private set; }

    public TenantPrincipalKind Kind { get; private set; } = TenantPrincipalKind.None;

    public void Resolve(Guid tenantId, Guid? userId, TenantPrincipalKind kind)
    {
        if (IsResolved)
        {
            throw new InvalidOperationException("Tenant context is already resolved for this scope.");
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));
        }

        _tenantId = tenantId;
        UserId = userId;
        Kind = kind;
        IsResolved = true;
    }
}
