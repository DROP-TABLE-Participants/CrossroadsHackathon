using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class InterviewMessageConfiguration : ControlPlaneEntityConfiguration<InterviewMessage>
{
    public override void Configure(EntityTypeBuilder<InterviewMessage> builder)
    {
        base.Configure(builder);
        builder.ToTable("interview_messages");
        builder.HasIndex(value => new { value.InterviewSessionId, value.TurnKey, value.Role }).IsUnique();
    }
}
