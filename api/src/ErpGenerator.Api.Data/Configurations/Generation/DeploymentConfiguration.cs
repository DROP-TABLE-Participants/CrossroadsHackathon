using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class DeploymentConfiguration : ControlPlaneEntityConfiguration<Deployment>
{
    public override void Configure(EntityTypeBuilder<Deployment> builder)
    {
        base.Configure(builder);
        builder.ToTable("deployments");
        builder.HasIndex(value => value.OperationId).IsUnique();
        builder.HasIndex(value => new { value.Status, value.LeaseExpiresAt });
    }
}
