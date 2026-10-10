namespace ErpGenerator.Runtime.Data.Databases.DeploymentLocks;

public sealed class TenantDeploymentLockOptions
{
    public int LockTtlMinutes { get; init; } = 15;
}
