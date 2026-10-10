using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Configurations.Tenants;

public sealed class RuntimeUsageEventConfiguration : ControlPlaneEntityConfiguration<RuntimeUsageEvent>
{
    public override void Configure(EntityTypeBuilder<RuntimeUsageEvent> builder)
    {
        base.Configure(builder);
        builder.ToTable("runtime_usage_events");
        builder.HasIndex(value => new { value.TenantEnvironmentId, value.OccurredAt });
    }
}
