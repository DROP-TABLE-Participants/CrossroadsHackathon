using System.Security.Cryptography;
using System.Text;

namespace ErpGenerator.Runtime.Shared.Deployment;

public static class RuntimeDeploymentBrokerLimits
{
    public const int MaximumRequestBytes = 12 * 1024 * 1024;
    public const int MaximumArtifactBytes = 8 * 1024 * 1024;
    public const int MaximumFileBytes = 1024 * 1024;
    public const int MaximumFiles = 512;
    public const int MaximumIdentifierLength = 128;
}

public static class RuntimeDeploymentBrokerFailureCodes
{
    public const string ErpPreflightTemporarilyUnavailable =
        "erp_preflight_temporarily_unavailable";
}

public sealed record RuntimeDeploymentTarget(
    string TenantId,
    string TenantStableId,
    string Host,
    string Environment,
    string DatabaseName);

public sealed record RuntimeDeploymentArtifactFile(
    string Path,
    string Content,
    string Sha256);

public sealed record RuntimeDeploymentArtifact(
    string BundleId,
    string BundleVersion,
    string BundleHash,
    string ArtifactHash,
    IReadOnlyList<RuntimeDeploymentArtifactFile> Files);

public sealed record RuntimeDeploymentActivation(
    string Url,
    string Email,
    DateTimeOffset ExpiresAt);

public sealed record RuntimeErpDeploymentBrokerRequest(
    string OperationId,
    string RunId,
    string GenerationProjectId,
    RuntimeDeploymentTarget Target,
    RuntimeDeploymentArtifact Artifact,
    string? OwnerEmail = null);

public sealed record RuntimeErpDeploymentBrokerResponse(
    bool Success,
    string Status,
    string OperationId,
    RuntimeDeploymentTarget Target,
    string? RuntimeUrl = null,
    string? BundleId = null,
    string? BundleVersion = null,
    string? BundleHash = null,
    string? DeploymentId = null,
    string? TestsStatus = null,
    string? TestsBundleVersion = null,
    string? TestsBundleHash = null,
    string? TestSuiteHash = null,
    RuntimeDeploymentActivation? Activation = null,
    bool AcceptedExisting = false,
    string? FailureCode = null,
    string? SafeError = null);

public sealed record RuntimeErpResetBrokerRequest(
    string OperationId,
    string RunId,
    string GenerationProjectId,
    RuntimeDeploymentTarget Target);

public sealed record RuntimeErpResetBrokerResponse(
    bool Success,
    string Status,
    string OperationId,
    RuntimeDeploymentTarget Target,
    string? RuntimeUrl = null,
    bool AcceptedExisting = false,
    string? FailureCode = null,
    string? SafeError = null);

public sealed record RuntimeOwnerActivationBrokerRequest(
    string OperationId,
    string RunId,
    string GenerationProjectId,
    RuntimeDeploymentTarget Target,
    string OwnerEmail);

public sealed record RuntimeOwnerActivationBrokerResponse(
    bool Success,
    string Status,
    string OperationId,
    RuntimeDeploymentTarget Target,
    string? RuntimeUrl = null,
    RuntimeDeploymentActivation? Activation = null,
    string? FailureCode = null,
    string? SafeError = null);

public static class RuntimeDeploymentArtifactHash
{
    public static string Compute(IEnumerable<RuntimeDeploymentArtifactFile> files)
    {
        var canonical = new StringBuilder();
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            canonical.Append(file.Path).Append('\0').Append(file.Sha256.ToLowerInvariant()).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public static string ComputeContent(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static string ComputeOperationId(params string?[] components)
    {
        var canonical = string.Join("\0", components.Select(component => component?.Trim() ?? string.Empty));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
