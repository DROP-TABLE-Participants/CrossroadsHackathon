using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class InterviewAnswer : ControlPlaneEntity
{
    public Guid InterviewRequestId { get; set; }
    public InterviewRequest InterviewRequest { get; set; } = null!;
    [MaxLength(160)] public string QuestionKey { get; set; } = string.Empty;
    [MaxLength(8000)] public string AnswerText { get; set; } = string.Empty;
    public string StructuredFactsJson { get; set; } = "[]";
}
