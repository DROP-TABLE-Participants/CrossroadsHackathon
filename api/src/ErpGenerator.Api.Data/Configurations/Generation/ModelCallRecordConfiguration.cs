using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class ModelCallRecordConfiguration : ControlPlaneEntityConfiguration<ModelCallRecord>
{
    public override void Configure(EntityTypeBuilder<ModelCallRecord> builder)
    {
        base.Configure(builder);
        builder.ToTable("model_call_records");
        builder.HasIndex(value => new { value.GenerationRunId, value.ControllerCallId }).IsUnique();
        builder.HasIndex(value => value.GenerationAgentExecutionAttemptId);
    }
}
