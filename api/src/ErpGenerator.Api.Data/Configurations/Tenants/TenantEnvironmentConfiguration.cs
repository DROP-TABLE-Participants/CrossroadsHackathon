using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Configurations.Tenants;

public sealed class TenantEnvironmentConfiguration : ControlPlaneEntityConfiguration<TenantEnvironment>
{
    public override void Configure(EntityTypeBuilder<TenantEnvironment> builder)
    {
        base.Configure(builder);
        builder.ToTable("tenant_environments");
        builder.HasIndex(value => value.Host).IsUnique();
        builder.HasIndex(value => new { value.TenantId, value.Environment }).IsUnique();
    }
}
