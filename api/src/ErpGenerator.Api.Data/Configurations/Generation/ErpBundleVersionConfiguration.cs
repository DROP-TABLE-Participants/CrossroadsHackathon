using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class ErpBundleVersionConfiguration : ControlPlaneEntityConfiguration<ErpBundleVersion>
{
    public override void Configure(EntityTypeBuilder<ErpBundleVersion> builder)
    {
        base.Configure(builder);
        builder.ToTable("erp_bundle_versions");
        builder.HasIndex(value => new { value.BundleId, value.Version }).IsUnique();
        builder.HasIndex(value => new { value.GenerationRunId, value.ContentHash }).IsUnique();
        builder.HasIndex(value => value.ContentHash);
        builder.HasIndex(value => value.GenerationArtifactVersionId).IsUnique();
    }
}
