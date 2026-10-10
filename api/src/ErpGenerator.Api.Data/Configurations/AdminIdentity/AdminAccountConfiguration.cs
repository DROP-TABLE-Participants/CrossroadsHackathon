using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Entities.AdminIdentity;

namespace ErpGenerator.Api.Data.Configurations.AdminIdentity;

public sealed class AdminAccountConfiguration : IEntityTypeConfiguration<AdminAccount>
{
    public void Configure(EntityTypeBuilder<AdminAccount> builder)
    {
        builder.ToTable("admin_accounts");
        builder.Property(value => value.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(value => value.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(value => value.Email).HasMaxLength(320);
        builder.Property(value => value.NormalizedEmail).HasMaxLength(320);
        builder.Property(value => value.UserName).HasMaxLength(320);
        builder.Property(value => value.NormalizedUserName).HasMaxLength(320);
        builder.Property(value => value.PhoneNumber).HasMaxLength(50);
        builder.HasIndex(value => value.NormalizedEmail).IsUnique();
    }
}
