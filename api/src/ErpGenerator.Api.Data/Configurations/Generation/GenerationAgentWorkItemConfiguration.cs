using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class GenerationAgentWorkItemConfiguration
    : ControlPlaneEntityConfiguration<GenerationAgentWorkItem>
{
    public override void Configure(EntityTypeBuilder<GenerationAgentWorkItem> builder)
    {
        base.Configure(builder);
        builder.ToTable("generation_agent_work_items");
        builder.Property(value => value.LeaseId).IsConcurrencyToken();
        builder.HasIndex(value => new { value.GenerationRunId, value.WorkItemKey }).IsUnique();
        builder.HasIndex(value => new { value.Status, value.NotBefore, value.Priority, value.CreatedAt });
        builder.HasIndex(value => new { value.Status, value.LeaseExpiresAt });
        builder.HasOne(value => value.ParentWorkItem)
            .WithMany(value => value.ChildWorkItems)
            .HasForeignKey(value => value.ParentWorkItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
