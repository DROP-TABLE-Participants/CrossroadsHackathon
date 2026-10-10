namespace ErpGenerator.Runtime.Shared.Compilation;

public sealed record BundleMigrationPlan(
    string? FromBundleVersion,
    string ToBundleVersion,
    string RiskLevel,
    IReadOnlyList<BundleMigrationOperation> Operations,
    IReadOnlyList<string> BlockedReasons)
{
    public bool IsBlocked => RiskLevel.Equals("blocked", StringComparison.OrdinalIgnoreCase);
}

public sealed record BundleMigrationOperation(
    string Type,
    string Risk,
    string? Entity = null,
    string? Field = null,
    string? From = null,
    string? To = null,
    bool? Nullable = null,
    bool Rollbackable = true,
    string? Reason = null);
