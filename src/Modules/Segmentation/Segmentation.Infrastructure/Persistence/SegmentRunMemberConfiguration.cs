using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Segmentation.Infrastructure.Persistence;

/// <summary>
/// segments.SegmentRunMembers: no surrogate id, no CreatedAt/UpdatedAt (H9) — the table is exactly
/// TenantId, RunId, ProfileId. Precedent: UserIdentityConfiguration.Ignore(i => i.Id).
/// </summary>
internal sealed class SegmentRunMemberConfiguration : IEntityTypeConfiguration<SegmentRunMember>
{
    public void Configure(EntityTypeBuilder<SegmentRunMember> builder)
    {
        builder.ToTable("SegmentRunMembers");

        builder.Ignore(m => m.Id);
        builder.Ignore(m => m.CreatedAt);
        builder.Ignore(m => m.UpdatedAt);
        builder.HasKey(m => new { m.RunId, m.ProfileId }).HasName("PK_SegmentRunMembers");

        builder.Property(m => m.TenantId).ValueGeneratedNever();

        // "which segments contain this profile": TenantId, ProfileId.
        builder.HasIndex(m => new { m.TenantId, m.ProfileId }).HasDatabaseName("IX_SegmentRunMembers_TenantId_ProfileId");

        builder.HasOne<SegmentRun>().WithMany().HasForeignKey(m => m.RunId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_SegmentRunMembers_RunId");
    }
}
