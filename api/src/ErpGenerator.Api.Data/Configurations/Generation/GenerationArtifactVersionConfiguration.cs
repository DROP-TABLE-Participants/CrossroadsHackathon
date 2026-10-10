using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationArtifactVersionConfiguration
    : ControlPlaneEntityConfiguration<GenerationArtifactVersion>
{
    public override void Configure(EntityTypeBuilder<GenerationArtifactVersion> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_artifact_versions");
        builder.HasIndex(value => new
        {
            value.GenerationRunId,
            value.ArtifactKey,
            value.Version
        }).IsUnique();
        builder.HasIndex(value => new
        {
            value.GenerationRunId,
            value.ArtifactType,
            value.ContentHash
        }).IsUnique();
        builder.HasIndex(value => value.ContentHash);
        builder.HasOne(value => value.SupersedesArtifactVersion)
            .WithMany(value => value.SupersedingVersions)
            .HasForeignKey(value => value.SupersedesArtifactVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
