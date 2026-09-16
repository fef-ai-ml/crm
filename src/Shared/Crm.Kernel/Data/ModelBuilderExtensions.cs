using System.Reflection;
using Crm.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Crm.Kernel.Data;

public static class ModelBuilderExtensions
{
    private static readonly MethodInfo SetFilterMethod = typeof(ModelBuilderExtensions)
        .GetMethod(nameof(SetFilter), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Adds the global query filter e => e.TenantId == tenant.TenantId to every root entity type derived from <see cref="Entity"/>.
    /// Evaluating the filter on an unresolved context throws <see cref="TenantContextMissingException"/>, so queries fail closed.
    /// </summary>
    public static ModelBuilder ApplyTenantFilters(this ModelBuilder modelBuilder, ITenantContext tenant)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.BaseType is not null || !typeof(Entity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            SetFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [modelBuilder, tenant]);
        }

        return modelBuilder;
    }

    private static void SetFilter<TEntity>(ModelBuilder modelBuilder, ITenantContext tenant)
        where TEntity : Entity
        => modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == tenant.TenantId);
}
