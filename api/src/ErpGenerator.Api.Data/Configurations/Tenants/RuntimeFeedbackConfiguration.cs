using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Configurations.Tenants;

public sealed class RuntimeFeedbackConfiguration : ControlPlaneEntityConfiguration<RuntimeFeedback>
{
    public override void Configure(EntityTypeBuilder<RuntimeFeedback> builder)
    {
        base.Configure(builder);
        builder.ToTable("runtime_feedback");
        builder.HasIndex(value => new { value.TenantEnvironmentId, value.CreatedAt });
    }
}
