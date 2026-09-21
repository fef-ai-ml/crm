using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Segmentation.Infrastructure.Persistence;

/// <summary>
/// segments.MembershipChanges. CreatedAt and UpdatedAt are not mapped (H9) — OccurredAt is the only timestamp.
/// FK_MembershipChanges_SegmentId is NoAction: segments are archived, never deleted (Segment.Archive keeps the
/// row), so the reference is always safe to enforce.
/// </summary>
internal sealed class MembershipChangeConfiguration : IEntityTypeConfiguration<MembershipChange>
{
    public void Configure(EntityTypeBuilder<MembershipChange> builder)
    {
        builder.ToTable("MembershipChanges", t =>
        {
            t.HasCheckConstraint("CK_MembershipChanges_Change", "[Change] IN ('Entered','Exited')");
            t.HasCheckConstraint("CK_MembershipChanges_Source", "[Source] IN ('Batch','Realtime')");
        });

        builder.Ignore(c => c.CreatedAt);
        builder.Ignore(c => c.UpdatedAt);
        builder.HasKey(c => c.Id).HasName("PK_MembershipChanges").IsClustered(false);

        // H11: unique clustered, so the natural write and read order (append, replay newest-first) needs no sort.
        builder.HasIndex(c => new { c.TenantId, c.SegmentId, c.OccurredAt, c.Id })
            .IsUnique().IsClustered()
            .IsDescending(false, false, true, true)
            .HasDatabaseName("CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id");

        // "which segments this profile entered/exited": TenantId, ProfileId.
        builder.HasIndex(c => new { c.TenantId, c.ProfileId }).HasDatabaseName("IX_MembershipChanges_TenantId_ProfileId");

        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Change).HasConversion<string>().HasMaxLength(8).IsUnicode(false);
        builder.Property(c => c.Source).HasConversion<string>().HasMaxLength(8).IsUnicode(false);
        builder.Property(c => c.OccurredAt).HasPrecision(3);

        builder.HasOne<Segment>().WithMany().HasForeignKey(c => c.SegmentId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_MembershipChanges_SegmentId");
    }
}
