using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationAgentExecutionAttemptConfiguration
    : ControlPlaneEntityConfiguration<GenerationAgentExecutionAttempt>
{
    public override void Configure(EntityTypeBuilder<GenerationAgentExecutionAttempt> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_agent_execution_attempts");
        builder.HasIndex(value => new
        {
            value.GenerationAgentWorkItemId,
            value.AttemptNumber
        }).IsUnique();
        builder.HasIndex(value => new { value.Status, value.StartedAt });
    }
}
