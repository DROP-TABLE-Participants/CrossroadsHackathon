using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Configurations.Tenants;

public sealed class OwnerActivationConfiguration : ControlPlaneEntityConfiguration<OwnerActivation>
{
    public override void Configure(EntityTypeBuilder<OwnerActivation> builder)
    {
        base.Configure(builder);
        builder.ToTable("owner_activations");
        builder.HasIndex(value => new { value.TenantEnvironmentId, value.Email }).IsUnique();
    }
}
