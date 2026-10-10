using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class InterviewRequestConfiguration : ControlPlaneEntityConfiguration<InterviewRequest>
{
    public override void Configure(EntityTypeBuilder<InterviewRequest> builder)
    {
        base.Configure(builder);
        builder.ToTable("interview_requests");
        builder.HasIndex(value => value.TokenHash).IsUnique();
        builder.HasOne(value => value.PendingWorkItem).WithOne(value => value.InterviewRequest).HasForeignKey<InterviewRequest>(value => value.PendingWorkItemId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(value => value.Participant).WithMany(value => value.InterviewRequests).HasForeignKey(value => value.ParticipantId).OnDelete(DeleteBehavior.SetNull);
    }
}
