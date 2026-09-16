using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Crm.Kernel.Data;

/// <summary>
/// Stamps TenantId/CreatedAt/UpdatedAt on inserts and refuses writes to rows of another tenant.
/// Register as scoped and attach with DbContextOptionsBuilder.AddInterceptors.
/// </summary>
public sealed class TenantSaveChangesInterceptor(ITenantContext tenant, IClock clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var entries = context.ChangeTracker
            .Entries<Entity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (entries.Count == 0)
        {
            return;
        }

        var tenantId = tenant.TenantId;
        var now = clock.UtcNow;

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.TenantId != Guid.Empty && entry.Entity.TenantId != tenantId)
                    {
                        throw Mismatch(entry, tenantId);
                    }

                    entry.Entity.TenantId = tenantId;
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    if (entry.Entity.TenantId != tenantId)
                    {
                        throw Mismatch(entry, tenantId);
                    }

                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Deleted:
                    if (entry.Entity.TenantId != tenantId)
                    {
                        throw Mismatch(entry, tenantId);
                    }

                    break;
            }
        }
    }

    private static TenantMismatchException Mismatch(EntityEntry<Entity> entry, Guid expected)
        => new(expected, entry.Entity.TenantId, entry.Metadata.ClrType.Name);
}
