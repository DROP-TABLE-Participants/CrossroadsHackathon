namespace ErpGenerator.Runtime.Data.Databases.DeploymentLocks;

public sealed record TenantDeploymentLockResult(
    bool Acquired,
    TenantDeploymentLockHandle? Handle,
    string? ExistingOperation,
    DateTimeOffset? ExistingExpiresAt,
    string? Message)
{
    public static TenantDeploymentLockResult Success(TenantDeploymentLockHandle handle) =>
        new(true, handle, null, null, null);

    public static TenantDeploymentLockResult Locked(
        string? existingOperation,
        DateTimeOffset? existingExpiresAt,
        string message) =>
        new(false, null, existingOperation, existingExpiresAt, message);
}
