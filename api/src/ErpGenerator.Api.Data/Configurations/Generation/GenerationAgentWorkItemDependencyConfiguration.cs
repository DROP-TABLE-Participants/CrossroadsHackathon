using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationAgentWorkItemDependencyConfiguration
    : ControlPlaneEntityConfiguration<GenerationAgentWorkItemDependency>
{
    public override void Configure(EntityTypeBuilder<GenerationAgentWorkItemDependency> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_agent_work_item_dependencies");
        builder.HasIndex(value => new
        {
            value.GenerationAgentWorkItemId,
            value.DependsOnWorkItemId
        }).IsUnique();
        builder.HasOne(value => value.GenerationAgentWorkItem)
            .WithMany(value => value.Dependencies)
            .HasForeignKey(value => value.GenerationAgentWorkItemId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(value => value.DependsOnWorkItem)
            .WithMany(value => value.Dependents)
            .HasForeignKey(value => value.DependsOnWorkItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
