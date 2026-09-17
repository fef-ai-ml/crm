using Crm.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Tenancy.Infrastructure.Configurations;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("ApiKeys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.Name).HasMaxLength(ApiKey.NameMaxLength).IsRequired();
        builder.Property(k => k.Prefix).HasColumnType("char(8)").IsRequired();
        builder.Property(k => k.KeyHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(k => k.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.HasIndex(k => k.Prefix).IsUnique().HasDatabaseName("UX_ApiKeys_Prefix");
        builder.HasIndex(k => k.TenantId).HasDatabaseName("IX_ApiKeys_TenantId");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(k => k.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(k => k.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
