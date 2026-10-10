using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Entities.AdminIdentity;

namespace ErpGenerator.Api.Data.Configurations.AdminIdentity;

public sealed class AdminRoleConfiguration : IEntityTypeConfiguration<AdminRole>
{
    public void Configure(EntityTypeBuilder<AdminRole> builder) => builder.ToTable("admin_roles");
}
