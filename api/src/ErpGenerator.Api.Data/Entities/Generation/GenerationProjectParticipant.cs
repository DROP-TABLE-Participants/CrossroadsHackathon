using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationProjectParticipant : ControlPlaneEntity
{
    public Guid GenerationProjectId { get; set; }
    public GenerationProject GenerationProject { get; set; } = null!;
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    [MaxLength(320)] public string? Email { get; set; }
    [MaxLength(120)] public string? RoleTitle { get; set; }
    [MaxLength(120)] public string? Department { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "planned";
    public ICollection<InterviewRequest> InterviewRequests { get; set; } = [];
}
