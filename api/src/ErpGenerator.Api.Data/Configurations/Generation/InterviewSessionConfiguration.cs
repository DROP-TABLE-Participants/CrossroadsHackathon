using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class InterviewSessionConfiguration : ControlPlaneEntityConfiguration<InterviewSession>
{
    public override void Configure(EntityTypeBuilder<InterviewSession> builder)
    {
        base.Configure(builder);
        builder.ToTable("interview_sessions");
    }
}
