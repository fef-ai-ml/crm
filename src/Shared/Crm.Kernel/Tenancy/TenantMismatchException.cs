namespace Crm.Kernel.Tenancy;

public sealed class TenantMismatchException : InvalidOperationException
{
    public TenantMismatchException(Guid expected, Guid actual, string entityType)
        : base($"Entity {entityType} belongs to tenant {actual} but the current scope is tenant {expected}.")
    {
        Expected = expected;
        Actual = actual;
    }

    public Guid Expected { get; }

    public Guid Actual { get; }
}
