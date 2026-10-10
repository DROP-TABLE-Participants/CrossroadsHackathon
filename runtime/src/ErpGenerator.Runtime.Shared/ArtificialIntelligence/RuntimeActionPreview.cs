namespace ErpGenerator.Runtime.Shared.ArtificialIntelligence;

public sealed record RuntimeActionPreview(
    Guid Id,
    string ToolKey,
    string Risk,
    string ResourceType,
    string ResourceKey,
    IReadOnlyList<RuntimeActionPreviewChange> Changes,
    IReadOnlyList<RuntimeToolEffect> Effects,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RuntimeEvidenceReference> Evidence,
    string BundleHash,
    DateTimeOffset ExpiresAt)
{
    public bool ExecutionEnabled { get; init; }

    public string IdempotencyKey { get; init; } = string.Empty;

    public string PermissionSnapshotHash { get; init; } = string.Empty;

    public string? RecordVersion { get; init; }
}

public sealed record RuntimeActionPreviewChange(
    string Field,
    object? CurrentValue,
    object? ProposedValue);

public sealed record RuntimeActionConfirmation(
    Guid PreviewId,
    string IdempotencyKey,
    string PermissionSnapshotHash,
    string BundleHash,
    string? RecordVersion);

public sealed record RuntimeActionExecutionReceipt(
    Guid PreviewId,
    string ToolKey,
    string IdempotencyKey,
    string Status,
    DateTimeOffset ExecutedAt,
    IReadOnlyList<RuntimeEvidenceReference> Evidence,
    string BundleHash);
