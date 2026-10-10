using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeOwnerActivationViewModel(
    bool Success,
    string Status,
    string OperationId,
    RuntimeDeploymentTargetViewModel Target,
    string? RuntimeUrl = null,
    RuntimeDeploymentActivationViewModel? Activation = null,
    string? FailureCode = null,
    string? SafeError = null);
