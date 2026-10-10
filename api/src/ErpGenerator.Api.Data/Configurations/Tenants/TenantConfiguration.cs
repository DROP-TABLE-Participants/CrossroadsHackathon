using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Configurations.Tenants;

public sealed class TenantConfiguration : ControlPlaneEntityConfiguration<Tenant>
{
    public override void Configure(EntityTypeBuilder<Tenant> builder)
    {
        base.Configure(builder);
        builder.ToTable("tenants");
        builder.HasIndex(value => value.StableId).IsUnique();
        builder.HasIndex(value => value.Slug).IsUnique();
        builder.HasOne(value => value.Owner).WithMany(value => value.Tenants).HasForeignKey(value => value.OwnerId).OnDelete(DeleteBehavior.Restrict);
    }
}
