namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeProductionPromotionReceiptViewModel(
    string ReceiptVersion,
    string ReceiptId,
    string OperationId,
    string RunId,
    string GenerationProjectId,
    string TenantStableId,
    string StagingEnvironmentIdentity,
    string ProductionEnvironmentIdentity,
    string BundleId,
    string BundleVersion,
    string BundleHash,
    string TestSuiteHash,
    string DeploymentId,
    string PayloadHash,
    string SignatureAlgorithm,
    string Signature);
