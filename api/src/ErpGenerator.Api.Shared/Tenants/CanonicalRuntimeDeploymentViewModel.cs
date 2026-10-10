using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record CanonicalRuntimeDeploymentViewModel(
    bool Success,
    string Status,
    string OperationId,
    RuntimeDeploymentTargetViewModel Target,
    string? RuntimeUrl = null,
    string? BundleId = null,
    string? BundleVersion = null,
    string? ArtifactHash = null,
    string? BundleHash = null,
    string? DeploymentId = null,
    string? TestsStatus = null,
    string? TestSuiteHash = null,
    RuntimeDeploymentActivationViewModel? Activation = null,
    string? FailureCode = null,
    string? SafeError = null);
