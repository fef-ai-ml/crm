namespace Crm.Kernel.Tenancy;

/// <summary>Raised when data access happens without a resolved tenant. Always a bug: the caller failed closed instead of leaking data.</summary>
public sealed class TenantContextMissingException : InvalidOperationException
{
    public TenantContextMissingException()
        : base("No tenant is resolved for the current scope; refusing to run a tenant-scoped operation.")
    {
    }
}
