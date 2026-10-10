using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;
using ErpGenerator.Api.Data.Entities.Identity;
using ErpGenerator.Api.Data.Entities.Tenants;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationProject : ControlPlaneEntity
{
    public Guid CustomerId { get; set; }
    public CustomerAccount Customer { get; set; } = null!;
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    [MaxLength(160)] public string CompanyName { get; set; } = string.Empty;
    [MaxLength(4000)] public string Goal { get; set; } = string.Empty;
    [MaxLength(64)] public string DiscoveryMode { get; set; } = "interview_first";
    [MaxLength(120)] public string? Industry { get; set; }
    [MaxLength(120)] public string? CompanyType { get; set; }
    [MaxLength(120)] public string? OwnerName { get; set; }
    [MaxLength(320)] public string? OwnerEmail { get; set; }
    [MaxLength(120)] public string? OwnerRoleTitle { get; set; }
    public bool GenerateWithAssumptions { get; set; }
    public bool DeployToRuntime { get; set; } = true;
    public DateTimeOffset? GenerationRequestedAt { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "draft";
    public ICollection<GenerationProjectInputDocument> InputDocuments { get; set; } = [];
    public ICollection<GenerationProjectInputSource> InputSources { get; set; } = [];
    public ICollection<GenerationProjectParticipant> Participants { get; set; } = [];
    public ICollection<InterviewRequest> InterviewRequests { get; set; } = [];
    public ICollection<GenerationRun> GenerationRuns { get; set; } = [];
}
