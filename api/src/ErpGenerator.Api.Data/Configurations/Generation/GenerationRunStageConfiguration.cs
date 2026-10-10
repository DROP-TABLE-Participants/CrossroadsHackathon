using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationRunStageConfiguration : ControlPlaneEntityConfiguration<GenerationRunStage>
{
    public override void Configure(EntityTypeBuilder<GenerationRunStage> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_run_stages");
        builder.HasIndex(value => new { value.GenerationRunId, value.Sequence }).IsUnique();
        builder.HasIndex(value => new { value.GenerationRunId, value.StageKey }).IsUnique();
    }
}
