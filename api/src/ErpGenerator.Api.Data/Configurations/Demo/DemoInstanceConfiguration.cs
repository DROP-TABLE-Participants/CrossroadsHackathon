using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Demo;

namespace ErpGenerator.Api.Data.Configurations.Demo;

public sealed class DemoInstanceConfiguration : ControlPlaneEntityConfiguration<DemoInstance>
{
    public override void Configure(EntityTypeBuilder<DemoInstance> builder)
    {
        base.Configure(builder);
        builder.ToTable("demo_instances");
        builder.HasIndex(value => value.TenantId).IsUnique();
        builder.HasIndex(value => new { value.CustomerId, value.Status });
        builder.HasOne(value => value.Customer).WithMany().HasForeignKey(value => value.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(value => value.Tenant).WithMany().HasForeignKey(value => value.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
