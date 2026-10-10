using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Configurations.Tenants;

public sealed class RuntimeDeploymentEventConfiguration : ControlPlaneEntityConfiguration<RuntimeDeploymentEvent>
{
    public override void Configure(EntityTypeBuilder<RuntimeDeploymentEvent> builder)
    {
        base.Configure(builder);
        builder.ToTable("runtime_deployment_events");
        builder.HasIndex(value => new { value.TenantEnvironmentId, value.CreatedAt });
    }
}
