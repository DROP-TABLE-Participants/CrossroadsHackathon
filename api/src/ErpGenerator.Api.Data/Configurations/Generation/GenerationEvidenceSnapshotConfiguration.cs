using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationEvidenceSnapshotConfiguration : ControlPlaneEntityConfiguration<GenerationEvidenceSnapshot>
{
    public override void Configure(EntityTypeBuilder<GenerationEvidenceSnapshot> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_evidence_snapshots");
        builder.HasIndex(value => new { value.GenerationRunId, value.Sequence }).IsUnique();
    }
}
