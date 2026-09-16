using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Crm.Kernel.Data;

public static class ModelBuilderExtensions
{
    private static readonly MethodInfo SetFilterMethod = typeof(ModelBuilderExtensions)
        .GetMethod(nameof(SetFilter), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Adds the global query filter e => e.TenantId == context.CurrentTenantId to every root entity type derived from
    /// <see cref="Entity"/>. The lambda references the DbContext instance, which EF Core substitutes with the
    /// current context at query time; an unresolved tenant therefore throws <see cref="Tenancy.TenantContextMissingException"/>
    /// on every query, so reads fail closed.
    /// </summary>
    public static ModelBuilder ApplyTenantFilters<TContext>(this ModelBuilder modelBuilder, TContext context)
        where TContext : DbContext, ITenantScopedDbContext
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.BaseType is not null || !typeof(Entity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            SetFilterMethod.MakeGenericMethod(entityType.ClrType, typeof(TContext)).Invoke(null, [modelBuilder, context]);
        }

        return modelBuilder;
    }

    private static void SetFilter<TEntity, TContext>(ModelBuilder modelBuilder, TContext context)
        where TEntity : Entity
        where TContext : DbContext, ITenantScopedDbContext
        => modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == context.CurrentTenantId);
}
