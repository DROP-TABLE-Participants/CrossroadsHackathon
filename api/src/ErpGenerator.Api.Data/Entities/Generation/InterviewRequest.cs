using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class InterviewRequest : ControlPlaneEntity
{
    public Guid GenerationProjectId { get; set; }
    public GenerationProject GenerationProject { get; set; } = null!;
    public Guid? PendingWorkItemId { get; set; }
    public PendingWorkItem? PendingWorkItem { get; set; }
    public Guid? ParticipantId { get; set; }
    public GenerationProjectParticipant? Participant { get; set; }
    [MaxLength(160)] public string QuestionKey { get; set; } = string.Empty;
    [MaxLength(4000)] public string Question { get; set; } = string.Empty;
    [MaxLength(320)] public string? RecipientEmail { get; set; }
    [MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = "pending";
    [MaxLength(32)] public string InviteDeliveryStatus { get; set; } = "not_sent";
    public int InviteDeliveryAttemptCount { get; set; }
    public DateTimeOffset? InviteDeliveredAt { get; set; }
    [MaxLength(1000)] public string? InviteDeliveryError { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public ICollection<InterviewSession> Sessions { get; set; } = [];
    public ICollection<InterviewAnswer> Answers { get; set; } = [];
}
