using Crm.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Profiles.Infrastructure.Persistence;

internal sealed class UserIdentityConfiguration : IEntityTypeConfiguration<UserIdentity>
{
    public void Configure(EntityTypeBuilder<UserIdentity> builder)
    {
        builder.ToTable("UserIdentities", t => t.HasCheckConstraint("CK_UserIdentities_Kind", "[Kind] IN ('external_id','anonymous_id','email','phone')"));

        // The table has no surrogate id: the data dictionary keys it by (TenantId, Kind, Value).
        builder.Ignore(i => i.Id);
        builder.Ignore(i => i.Key);
        builder.HasKey(i => new { i.TenantId, i.Kind, i.Value }).HasName("PK_UserIdentities");

        builder.Property(i => i.TenantId).ValueGeneratedNever();
        builder.Property(i => i.Kind).HasMaxLength(16).IsUnicode(false);
        builder.Property(i => i.Value).HasMaxLength(256);
        builder.Property(i => i.CreatedAt).HasPrecision(3);
        builder.Property(i => i.UpdatedAt).HasPrecision(3);

        builder.HasOne<Profile>().WithMany().HasForeignKey(i => i.ProfileId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_UserIdentities_ProfileId");
        builder.HasIndex(i => i.ProfileId).HasDatabaseName("IX_UserIdentities_ProfileId");
    }
}
