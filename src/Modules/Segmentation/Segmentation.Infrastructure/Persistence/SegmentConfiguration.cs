using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Segmentation.Infrastructure.Persistence;

/// <summary>
/// segments.Segments. FK_Segments_CurrentRunId and FK_SegmentRuns_SegmentId form a cycle between this table and
/// SegmentRuns; the migration creates both tables first and adds whichever FK closes the cycle afterwards.
/// </summary>
internal sealed class SegmentConfiguration : IEntityTypeConfiguration<Segment>
{
    public void Configure(EntityTypeBuilder<Segment> builder)
    {
        builder.ToTable("Segments", t =>
        {
            t.HasCheckConstraint("CK_Segments_Definition_IsJson", "ISJSON([Definition]) = 1");
            t.HasCheckConstraint("CK_Segments_Status", "[Status] IN ('Draft','Active','Archived')");
        });

        builder.HasKey(s => s.Id).HasName("PK_Segments").IsClustered(false);
        builder.HasIndex(s => new { s.TenantId, s.Id }).IsUnique().IsClustered().HasDatabaseName("CX_Segments_TenantId_Id");
        builder.HasIndex(s => new { s.TenantId, s.Name }).IsUnique().HasDatabaseName("UX_Segments_TenantId_Name");

        // Keyset index for the segment list (H10/H11): TenantId, CreatedAt DESC, Id DESC as tiebreak, plus the
        // list-row projection so the read never touches the base table.
        builder.HasIndex(s => new { s.TenantId, s.CreatedAt, s.Id })
            .IsDescending(false, true, true)
            .IncludeProperties(s => new { s.Name, s.Status, s.MemberCount, s.LastRunAt, s.IsRealtimeEligible })
            .HasDatabaseName("IX_Segments_TenantId_CreatedAt_Id");

        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(128);
        builder.Property(s => s.Description).HasMaxLength(1024);
        builder.Property(s => s.Definition).HasColumnType("nvarchar(max)");
        builder.Property(s => s.DefinitionHash).HasColumnType("binary(32)");
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(s => s.MemberCount).HasDefaultValue(0);
        builder.Property(s => s.LastRunAt).HasPrecision(3);
        builder.Property(s => s.CreatedAt).HasPrecision(3);
        builder.Property(s => s.UpdatedAt).HasPrecision(3);

        builder.HasOne<SegmentRun>().WithMany().HasForeignKey(s => s.CurrentRunId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_Segments_CurrentRunId");
    }
}
