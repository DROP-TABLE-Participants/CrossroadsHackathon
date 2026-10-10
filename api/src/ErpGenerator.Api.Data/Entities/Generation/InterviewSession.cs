using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class InterviewSession : ControlPlaneEntity
{
    public Guid InterviewRequestId { get; set; }
    public InterviewRequest InterviewRequest { get; set; } = null!;
    [MaxLength(32)] public string Status { get; set; } = "active";
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<InterviewMessage> Messages { get; set; } = [];
}
