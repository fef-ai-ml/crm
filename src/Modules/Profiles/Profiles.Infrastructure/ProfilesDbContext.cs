using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Crm.Profiles.Infrastructure;

public sealed class ProfilesDbContext(DbContextOptions<ProfilesDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "profiles";

    public Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        // Entity configurations are added by the Profiles plan.
        modelBuilder.ApplyTenantFilters(this);
    }
}
