using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationProjectParticipantConfiguration : ControlPlaneEntityConfiguration<GenerationProjectParticipant>
{
    public override void Configure(EntityTypeBuilder<GenerationProjectParticipant> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_project_participants");
        builder.HasIndex(value => new { value.GenerationProjectId, value.Email });
    }
}
