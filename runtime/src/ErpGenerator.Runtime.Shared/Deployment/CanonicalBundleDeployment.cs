namespace ErpGenerator.Runtime.Shared.Deployment;

public sealed record CanonicalBundleDeploymentRequest(
    string OperationId,
    string RunId,
    string GenerationProjectId,
    RuntimeDeploymentTarget Target,
    string ArtifactHash,
    CanonicalGeneratedErpBundle Bundle,
    string? OwnerEmail = null);

public sealed record CanonicalBundleDeploymentResponse(
    bool Success,
    string Status,
    string OperationId,
    RuntimeDeploymentTarget Target,
    string? RuntimeUrl = null,
    string? BundleId = null,
    string? BundleVersion = null,
    string? ArtifactHash = null,
    string? BundleHash = null,
    string? DeploymentId = null,
    string? TestsStatus = null,
    string? TestSuiteHash = null,
    RuntimeDeploymentActivation? Activation = null,
    string? FailureCode = null,
    string? SafeError = null);

public sealed record CanonicalGeneratedErpBundle(
    string Name,
    string Summary,
    IReadOnlyList<string> Modules,
    IReadOnlyList<CanonicalGeneratedEntity> Entities,
    IReadOnlyList<CanonicalGeneratedWorkflow> Workflows,
    IReadOnlyList<CanonicalGeneratedReport> Reports,
    IReadOnlyList<CanonicalGeneratedDashboard> Dashboards,
    IReadOnlyList<CanonicalGeneratedPermission> Permissions,
    IReadOnlyList<CanonicalGeneratedAcceptanceTest> AcceptanceTests,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OpenIssues);

public sealed record CanonicalGeneratedEntity(
    string Key,
    string Label,
    IReadOnlyList<CanonicalGeneratedField> Fields);
public sealed record CanonicalGeneratedField(
    string Key,
    string Label,
    string Type,
    bool Required,
    string? References);
public sealed record CanonicalGeneratedWorkflow(
    string Key,
    string Label,
    string EntityKey,
    string TriggerAction,
    string? ApprovalRole,
    string? StatusField,
    string? ApprovedValue,
    IReadOnlyList<string> Steps);
public sealed record CanonicalGeneratedReport(
    string Key,
    string Label,
    string EntityKey,
    IReadOnlyList<string> Columns);
public sealed record CanonicalGeneratedDashboard(
    string Key,
    string Label,
    IReadOnlyList<string> Reports);
public sealed record CanonicalGeneratedPermission(string Role, IReadOnlyList<string> Grants);
public sealed record CanonicalGeneratedAcceptanceTest(
    string Key,
    string Label,
    string Kind,
    string? EntityKey,
    string? ReportKey,
    IReadOnlyList<CanonicalGeneratedAcceptanceValue> Values);
public sealed record CanonicalGeneratedAcceptanceValue(string FieldKey, string Value);
