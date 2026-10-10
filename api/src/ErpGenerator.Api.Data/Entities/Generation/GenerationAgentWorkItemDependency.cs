using ErpGenerator.Api.Data.Entities.Common;

namespace ErpGenerator.Api.Data.Entities.Generation;

public sealed class GenerationAgentWorkItemDependency : ControlPlaneEntity
{
    public Guid GenerationAgentWorkItemId { get; set; }
    public GenerationAgentWorkItem GenerationAgentWorkItem { get; set; } = null!;
    public Guid DependsOnWorkItemId { get; set; }
    public GenerationAgentWorkItem DependsOnWorkItem { get; set; } = null!;
}
