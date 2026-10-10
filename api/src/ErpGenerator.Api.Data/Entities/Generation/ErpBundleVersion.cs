using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class ErpBundleVersion : ControlPlaneEntity
{
    public Guid GenerationRunId { get; set; }
    public GenerationRun GenerationRun { get; set; } = null!;
    public Guid GenerationArtifactVersionId { get; set; }
    public GenerationArtifactVersion GenerationArtifactVersion { get; set; } = null!;
    [MaxLength(120)] public string BundleId { get; set; } = string.Empty;
    public int Version { get; set; }
    [MaxLength(64)] public string ContentHash { get; set; } = string.Empty;
    [MaxLength(4000)] public string? Summary { get; set; }
    public ICollection<Deployment> Deployments { get; set; } = [];
}
