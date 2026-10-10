using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationProjectInputSourceConfiguration : ControlPlaneEntityConfiguration<GenerationProjectInputSource>
{
    public override void Configure(EntityTypeBuilder<GenerationProjectInputSource> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_project_input_sources");
        builder.HasIndex(value => new { value.GenerationProjectId, value.ProviderConfigKey, value.ConnectionId }).IsUnique();
        builder.Property(value => value.ScopeMode).HasDefaultValue("unconfigured");
        builder.Property(value => value.SelectedExternalIdsJson).HasDefaultValue("[]");
        builder.Property(value => value.ExcludedExternalIdsJson).HasDefaultValue("[]");
        builder.Property(value => value.IncludeDescendants).HasDefaultValue(true);
        builder.Property(value => value.IncludeNewContent).HasDefaultValue(true);
    }
}
