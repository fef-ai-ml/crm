using Crm.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Tenancy.Infrastructure.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(Tenant.NameMaxLength).IsRequired();
        builder.Property(t => t.Slug).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("UX_Tenants_Slug");
    }
}
