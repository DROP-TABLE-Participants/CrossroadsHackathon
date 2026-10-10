using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record CanonicalRuntimeDeploymentInputModel(
    string OperationId,
    string RunId,
    string GenerationProjectId,
    RuntimeDeploymentTargetViewModel Target,
    string ArtifactHash,
    GeneratedErpBundle Bundle,
    string? OwnerEmail = null);
