using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Tenancy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Crm.Tenancy.Infrastructure;

/// <summary>Schema <c>identity</c>: ASP.NET Core Identity tables plus Tenants, ApiKeys, RefreshTokens.</summary>
public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ITenantContext tenant)
    : IdentityDbContext<AppUser, IdentityRole, string>(options), ITenantScopedDbContext
{
    public const string Schema = "identity";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_identity";
    public const string AdminRoleId = "0192f0a0-0000-7000-8000-000000000001";
    public const string ViewerRoleId = "0192f0a0-0000-7000-8000-000000000002";

    public Guid CurrentTenantId => tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HavePrecision(3);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.ApplyConfigurationsFromAssembly(typeof(TenancyDbContext).Assembly);

        builder.Entity<AppUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(128);
            user.HasIndex(u => u.TenantId).HasDatabaseName("IX_AspNetUsers_TenantId");
            user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);

            // AppUser is not a Kernel Entity, so ApplyTenantFilters skips it; the filter reads this context per query.
            user.HasQueryFilter(u => u.TenantId == CurrentTenantId);
        });

        builder.Entity<IdentityRole>().HasData(
            new IdentityRole { Id = AdminRoleId, Name = Crm.Tenancy.Domain.Roles.Admin, NormalizedName = "ADMIN", ConcurrencyStamp = AdminRoleId },
            new IdentityRole { Id = ViewerRoleId, Name = Crm.Tenancy.Domain.Roles.Viewer, NormalizedName = "VIEWER", ConcurrencyStamp = ViewerRoleId });

        builder.ApplyTenantFilters(this);
    }
}
