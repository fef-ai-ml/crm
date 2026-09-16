using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Crm.Segmentation.Infrastructure;

public sealed class SegmentationDbContext(DbContextOptions<SegmentationDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "segments";

    public Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        // Entity configurations are added by the Segmentation plan.
        modelBuilder.ApplyTenantFilters(this);
    }
}
