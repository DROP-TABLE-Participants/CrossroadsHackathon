using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeOwnerActivationInputModel(
    string OperationId,
    string RunId,
    string GenerationProjectId,
    RuntimeDeploymentTargetViewModel Target,
    string OwnerEmail);
