using System.ComponentModel.DataAnnotations;
using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationProjectInputDocument : ControlPlaneEntity
{
    public Guid GenerationProjectId { get; set; }
    public GenerationProject GenerationProject { get; set; } = null!;
    [MaxLength(240)] public string Title { get; set; } = string.Empty;
    [MaxLength(64)] public string Kind { get; set; } = "evidence";
    public string Content { get; set; } = string.Empty;
    [MaxLength(64)] public string ContentSha256 { get; set; } = string.Empty;
}
