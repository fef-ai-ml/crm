using Crm.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Profiles.Infrastructure.Persistence;

/// <summary>
/// profiles.Events, partitioned by month on OccurredAt. EF needs a key, the database needs a partition-aligned
/// unique index: the key is named UX_Events_Id and the migration creates it as that index instead of a PRIMARY KEY.
/// FK_Events_ProfileId is created by the migration's SQL so EF adds no IX_Events_ProfileId. The clustered key ends
/// in Id so it is unique and matches the timeline's ORDER BY OccurredAt DESC, Id DESC as a backward scan.
/// </summary>
internal sealed class EventRecordConfiguration : IEntityTypeConfiguration<EventRecord>
{
    public void Configure(EntityTypeBuilder<EventRecord> builder)
    {
        builder.ToTable("Events", t => t.HasCheckConstraint("CK_Events_Properties_IsJson", "ISJSON([Properties]) = 1"));

        builder.HasKey(e => new { e.Id, e.OccurredAt }).HasName("UX_Events_Id").IsClustered(false);
        builder.HasIndex(e => new { e.TenantId, e.ProfileId, e.OccurredAt, e.Id }).IsUnique().IsClustered().HasDatabaseName("CX_Events_TenantId_ProfileId_OccurredAt_Id");
        builder.HasIndex(e => new { e.TenantId, e.Name, e.OccurredAt }).IncludeProperties(e => e.ProfileId).HasDatabaseName("IX_Events_TenantId_Name_OccurredAt");

        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Name).HasMaxLength(64);
        builder.Property(e => e.Properties).HasColumnType("nvarchar(max)").HasDefaultValueSql("N'{}'");
        builder.Property(e => e.OccurredAt).HasPrecision(3);
        builder.Property(e => e.ReceivedAt).HasPrecision(3);
        builder.Property(e => e.CreatedAt).HasPrecision(3);
        builder.Property(e => e.UpdatedAt).HasPrecision(3);
    }
}
