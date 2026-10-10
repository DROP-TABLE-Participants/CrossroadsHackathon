using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeProductionPromotionInputModel(
    string OperationId,
    string RunId,
    string GenerationProjectId,
    RuntimeDeploymentTargetViewModel StagingTarget,
    RuntimeDeploymentTargetViewModel ProductionTarget,
    string BundleId,
    string BundleVersion,
    string BundleHash,
    string TestSuiteHash,
    string? ExpectedBaseBundleVersion = null,
    string? ExpectedBaseBundleHash = null,
    string? OwnerEmail = null);
