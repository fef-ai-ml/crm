namespace Crm.Kernel.Tenancy;

public enum TenantPrincipalKind
{
    None,
    User,
    ApiKey,
}

/// <summary>The tenant the current request or message is acting for. Scoped per request / per consumed message.</summary>
public interface ITenantContext
{
    bool IsResolved { get; }

    /// <summary>Throws <see cref="TenantContextMissingException"/> when unresolved. Never returns Guid.Empty.</summary>
    Guid TenantId { get; }

    Guid? UserId { get; }

    TenantPrincipalKind Kind { get; }
}
