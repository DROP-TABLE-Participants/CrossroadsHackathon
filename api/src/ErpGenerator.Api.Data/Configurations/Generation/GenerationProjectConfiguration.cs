using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationProjectConfiguration : ControlPlaneEntityConfiguration<GenerationProject>
{
    public override void Configure(EntityTypeBuilder<GenerationProject> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_projects");
        builder.HasIndex(value => value.GenerationRequestedAt);
        builder.Property(value => value.DeployToRuntime).HasDefaultValue(true);
        builder.HasOne(value => value.Customer).WithMany(value => value.GenerationProjects).HasForeignKey(value => value.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}
