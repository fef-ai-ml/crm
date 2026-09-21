using System.Reflection;
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;

namespace Crm.Segmentation.Infrastructure;

public sealed class SegmentationDbContext(DbContextOptions<SegmentationDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "segments";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_segments";

    public Guid CurrentTenantId => tenant.TenantId;

    public DbSet<Segment> Segments => Set<Segment>();

    public DbSet<SegmentRun> SegmentRuns => Set<SegmentRun>();

    public DbSet<SegmentRunMember> SegmentRunMembers => Set<SegmentRunMember>();

    public DbSet<MembershipChange> MembershipChanges => Set<MembershipChange>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        modelBuilder.ApplyTenantFilters(this);
    }
}
