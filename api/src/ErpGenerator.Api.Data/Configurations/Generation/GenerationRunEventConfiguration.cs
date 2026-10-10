using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationRunEventConfiguration : ControlPlaneEntityConfiguration<GenerationRunEvent>
{
    public override void Configure(EntityTypeBuilder<GenerationRunEvent> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_run_events");
        builder.HasIndex(value => new { value.GenerationRunId, value.Sequence }).IsUnique();
        builder.HasIndex(value => new { value.EventType, value.CreatedAt });
    }
}
