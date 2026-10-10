using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class InterviewMessage : ControlPlaneEntity
{
    public Guid InterviewSessionId { get; set; }
    public InterviewSession InterviewSession { get; set; } = null!;
    [MaxLength(16)] public string Role { get; set; } = "user";
    [MaxLength(32)] public string MessageKind { get; set; } = "answer";
    [MaxLength(160)] public string? QuestionKey { get; set; }
    [MaxLength(160)] public string? TurnKey { get; set; }
    [MaxLength(8000)] public string Content { get; set; } = string.Empty;
}
