using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationRunConfiguration : ControlPlaneEntityConfiguration<GenerationRun>
{
    public override void Configure(EntityTypeBuilder<GenerationRun> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_runs");
        builder.Property(value => value.LeaseId).IsConcurrencyToken();
        builder.Property(value => value.LastEventSequence).IsConcurrencyToken();
        builder.HasIndex(value => new { value.Status, value.LeaseExpiresAt });
        builder.HasIndex(value => new { value.Status, value.NextAttemptAt, value.CreatedAt });
        builder.HasIndex(value => new { value.Status, value.ResumeFailureCount, value.UpdatedAt });
    }
}
