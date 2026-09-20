using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Profiles.Domain;
using Crm.Profiles.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Crm.Profiles.Infrastructure;

public sealed class ProfilesDbContext(DbContextOptions<ProfilesDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "profiles";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_profiles";

    public Guid CurrentTenantId => tenant.TenantId;

    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();

    public DbSet<EventRecord> Events => Set<EventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new ProfileConfiguration());
        modelBuilder.ApplyConfiguration(new UserIdentityConfiguration());
        modelBuilder.ApplyConfiguration(new EventRecordConfiguration());
        modelBuilder.ApplyTenantFilters(this);
    }
}
