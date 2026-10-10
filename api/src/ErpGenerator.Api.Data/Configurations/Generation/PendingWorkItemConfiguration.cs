using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class PendingWorkItemConfiguration : ControlPlaneEntityConfiguration<PendingWorkItem>
{
    public override void Configure(EntityTypeBuilder<PendingWorkItem> builder)
    {
        base.Configure(builder);
        builder.ToTable("pending_work_items");
        builder.HasIndex(value => new { value.GenerationRunId, value.Key }).IsUnique();
    }
}
