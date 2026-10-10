using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationEvidenceSnapshotItemConfiguration : ControlPlaneEntityConfiguration<GenerationEvidenceSnapshotItem>
{
    public override void Configure(EntityTypeBuilder<GenerationEvidenceSnapshotItem> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_evidence_snapshot_items");
        builder.HasIndex(value => new { value.GenerationEvidenceSnapshotId, value.SourceType, value.SourceEntityId });
    }
}
