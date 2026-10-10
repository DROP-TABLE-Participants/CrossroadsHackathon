namespace ErpGenerator.Api.Shared.Tenants;

public sealed record CuratedRuntimeFile(string Path, string Content, string Sha256);
public sealed record CuratedRuntimeDeploymentInputModel(string OperationId, string RunId, string GenerationProjectId, RuntimeDeploymentTargetViewModel Target, IReadOnlyList<CuratedRuntimeFile> Files);
public sealed record RuntimeGuestEntryInputModel(string OperationId, RuntimeDeploymentTargetViewModel Target, string CustomerId, string DisplayName);
public sealed record RuntimeGuestEntryViewModel(string Ticket, string EntryUrl, DateTimeOffset ExpiresAt);
public sealed record CuratedRuntimeDeploymentViewModel(bool Success, string Status, string OperationId, RuntimeDeploymentTargetViewModel Target, string? RuntimeUrl = null, string? BundleId = null, string? BundleVersion = null, string? BundleHash = null, string? DeploymentId = null, string? TestsStatus = null, string? TestsBundleVersion = null, string? TestsBundleHash = null, string? TestSuiteHash = null, string? FailureCode = null, string? SafeError = null);
