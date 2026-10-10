namespace ErpGenerator.Runtime.Shared.Deployment;

public sealed record RuntimeCuratedDeploymentRequest(
    string OperationId, string RunId, string GenerationProjectId,
    RuntimeDeploymentTarget Target, IReadOnlyList<RuntimeDeploymentArtifactFile> Files);

public sealed record RuntimeGuestEntryRequest(
    string OperationId, RuntimeDeploymentTarget Target, string CustomerId, string DisplayName);

public sealed record RuntimeGuestEntryResponse(string Ticket, string EntryUrl, DateTimeOffset ExpiresAt);
