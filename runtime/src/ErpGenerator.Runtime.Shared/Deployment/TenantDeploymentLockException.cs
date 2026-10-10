namespace ErpGenerator.Runtime.Shared.Deployment;

public sealed class TenantDeploymentLockException(
    string environment,
    string operation,
    string message,
    string? existingOperation = null,
    DateTimeOffset? existingExpiresAt = null)
    : InvalidOperationException(message)
{
    public string Environment { get; } = environment;

    public string Operation { get; } = operation;

    public string? ExistingOperation { get; } = existingOperation;

    public DateTimeOffset? ExistingExpiresAt { get; } = existingExpiresAt;
}
