using System.Text.Json;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Shared.Runtime;

public sealed record RuntimeMetadata(
    string TenantSlug,
    string Host,
    string Environment,
    string? BundleId,
    string? BundleVersion,
    string? BundleHash,
    IReadOnlyList<JsonElement> Entities,
    IReadOnlyList<JsonElement> Views,
    IReadOnlyList<JsonElement> Workflows,
    IReadOnlyList<JsonElement> Actions,
    IReadOnlyList<JsonElement> Reports,
    JsonElement? Dashboard);

public sealed record RuntimeBundleDeploymentResult(
    long? BundleVersionId,
    long MigrationPlanId,
    long DeploymentId,
    string BundleId,
    string Version,
    string BundleHash,
    string Status,
    BundleMigrationPlan Plan,
    string? FailureCode = null,
    string? SafeError = null);

public sealed record RuntimeRollbackResult(
    long DeploymentId,
    string FromVersion,
    string ToVersion,
    string BundleHash);

public sealed record RuntimeBundleDeploymentHistoryItem(
    long Id,
    string Environment,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorMessage,
    string? BundleId,
    string? BundleVersion,
    string? BundleHash,
    string? RiskLevel,
    IReadOnlyList<BundleMigrationOperation> Operations,
    IReadOnlyList<string> BlockedReasons);

public sealed record RuntimeUser(
    long Id,
    string Email,
    string DisplayName,
    string PasswordHash,
    string Role,
    string Status,
    IReadOnlyList<string> Roles);
