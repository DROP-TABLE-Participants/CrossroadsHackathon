using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Entities.Identity;

namespace ErpGenerator.Api.Data.Configurations.Identity;

public sealed class CustomerAccountConfiguration : IEntityTypeConfiguration<CustomerAccount>
{
    public void Configure(EntityTypeBuilder<CustomerAccount> builder)
    {
        builder.ToTable("customer_accounts");
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
