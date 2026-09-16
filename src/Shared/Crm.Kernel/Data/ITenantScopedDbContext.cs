namespace Crm.Kernel.Data;

/// <summary>
/// Implemented by every module DbContext. Query filters read the tenant through the context instance
/// so EF Core re-evaluates them per query instead of caching the first instance's tenant in the model.
/// </summary>
public interface ITenantScopedDbContext
{
    /// <summary>Throws <see cref="Tenancy.TenantContextMissingException"/> when no tenant is resolved.</summary>
    Guid CurrentTenantId { get; }
}
