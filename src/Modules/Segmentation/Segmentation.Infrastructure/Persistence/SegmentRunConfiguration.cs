using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Segmentation.Infrastructure.Persistence;

/// <summary>segments.SegmentRuns. Entity.Id maps to column RunId (H9); the PK keeps EF's convention name.</summary>
internal sealed class SegmentRunConfiguration : IEntityTypeConfiguration<SegmentRun>
{
    public void Configure(EntityTypeBuilder<SegmentRun> builder)
    {
        builder.ToTable("SegmentRuns", t =>
        {
            t.HasCheckConstraint("CK_SegmentRuns_Status", "[Status] IN ('Queued','Running','Finished','Failed')");
            t.HasCheckConstraint("CK_SegmentRuns_Trigger", "[Trigger] IN ('Manual','Schedule')");
        });

        builder.Property(r => r.Id).HasColumnName("RunId").ValueGeneratedNever();
        builder.HasKey(r => r.Id).HasName("PK_SegmentRuns");

        // Keyset index for the run history of a segment (H10/H11): TenantId, SegmentId, CreatedAt DESC, RunId DESC.
        builder.HasIndex(r => new { r.TenantId, r.SegmentId, r.CreatedAt, r.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("IX_SegmentRuns_TenantId_SegmentId_CreatedAt_RunId");

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(r => r.StartedAt).HasPrecision(3);
        builder.Property(r => r.FinishedAt).HasPrecision(3);
        builder.Property(r => r.CompiledSql).HasColumnType("nvarchar(max)");
        builder.Property(r => r.Error).HasMaxLength(4000);
        builder.Property(r => r.CreatedAt).HasPrecision(3);
        builder.Property(r => r.UpdatedAt).HasPrecision(3);

        builder.HasOne<Segment>().WithMany().HasForeignKey(r => r.SegmentId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_SegmentRuns_SegmentId");
    }
}
