using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationProjectInputDocumentConfiguration : ControlPlaneEntityConfiguration<GenerationProjectInputDocument>
{
    public override void Configure(EntityTypeBuilder<GenerationProjectInputDocument> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_project_input_documents");
    }
}
