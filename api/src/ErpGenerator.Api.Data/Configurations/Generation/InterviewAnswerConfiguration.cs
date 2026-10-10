using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpGenerator.Api.Data.Configurations.Common;
using ErpGenerator.Api.Data.Entities.Generation;

namespace ErpGenerator.Api.Data.Configurations.Generation;

public sealed class InterviewAnswerConfiguration : ControlPlaneEntityConfiguration<InterviewAnswer>
{
    public override void Configure(EntityTypeBuilder<InterviewAnswer> builder)
    {
        base.Configure(builder);
        builder.ToTable("interview_answers");
    }
}
