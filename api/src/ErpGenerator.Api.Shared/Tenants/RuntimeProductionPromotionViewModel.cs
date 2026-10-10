using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeProductionPromotionViewModel(
    bool Success,
    string Status,
    string OperationId,
    RuntimeDeploymentTargetViewModel StagingTarget,
    RuntimeDeploymentTargetViewModel ProductionTarget,
    string? RuntimeUrl = null,
    string? BundleId = null,
    string? BundleVersion = null,
    string? BundleHash = null,
    string? TestSuiteHash = null,
    string? DeploymentId = null,
    RuntimeDeploymentActivationViewModel? Activation = null,
    bool AcceptedExisting = false,
    string? FailureCode = null,
    string? SafeError = null,
    RuntimeProductionPromotionReceiptViewModel? PromotionReceipt = null);
