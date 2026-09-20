using Crm.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Profiles.Infrastructure.Persistence;

/// <summary>profiles.Profiles. FK_Profiles_MergedIntoId is created by the migration's SQL, not modelled, so EF adds no index for it.</summary>
internal sealed class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.ToTable("Profiles", t => t.HasCheckConstraint("CK_Profiles_Attributes_IsJson", "ISJSON([Attributes]) = 1"));

        builder.HasKey(p => p.Id).HasName("PK_Profiles").IsClustered(false);
        builder.HasIndex(p => new { p.TenantId, p.Id }).IsUnique().IsClustered().HasDatabaseName("CX_Profiles_TenantId_Id");

        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.ExternalId).HasMaxLength(128);
        builder.Property(p => p.Email).HasMaxLength(256);
        builder.Property(p => p.Phone).HasMaxLength(32);
        builder.Property(p => p.Attributes).HasColumnType("nvarchar(max)").HasDefaultValueSql("N'{}'");
        builder.Property(p => p.EventCount).HasDefaultValue(0L);
        builder.Property(p => p.FirstSeenAt).HasPrecision(3);
        builder.Property(p => p.LastSeenAt).HasPrecision(3);
        builder.Property(p => p.CreatedAt).HasPrecision(3);
        builder.Property(p => p.UpdatedAt).HasPrecision(3);

        builder.HasIndex(p => new { p.TenantId, p.ExternalId }).IsUnique().HasFilter("[ExternalId] IS NOT NULL").HasDatabaseName("UX_Profiles_TenantId_ExternalId");
        builder.HasIndex(p => new { p.TenantId, p.Email }).IsUnique().HasFilter("[Email] IS NOT NULL").HasDatabaseName("UX_Profiles_TenantId_Email");
        builder.HasIndex(p => new { p.TenantId, p.Phone }).IsUnique().HasFilter("[Phone] IS NOT NULL").HasDatabaseName("UX_Profiles_TenantId_Phone");
        builder.HasIndex(p => new { p.TenantId, p.LastSeenAt }).IsDescending(false, true).HasDatabaseName("IX_Profiles_TenantId_LastSeenAt");

        foreach (var key in PromotedAttributes.Keys)
        {
            var column = PromotedAttributes.ColumnName(key);
            builder.Property<string?>(column).HasMaxLength(256).HasComputedColumnSql(PromotedAttributes.ColumnSql(key), stored: true);
            builder.HasIndex(nameof(Profile.TenantId), column).HasDatabaseName($"IX_Profiles_TenantId_{column}");
        }
    }
}
