using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.DeploymentLocks;

public sealed record TenantDeploymentLockHandle(
    Guid LockId,
    string LockKey,
    ResolvedTenantEnvironment Tenant,
    string Environment,
    string Operation,
    string OwnerId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);
