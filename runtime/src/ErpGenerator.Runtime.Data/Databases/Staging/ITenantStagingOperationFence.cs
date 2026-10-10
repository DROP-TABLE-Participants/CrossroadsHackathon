using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Staging;

public interface ITenantStagingOperationFence
{
    Task<IAsyncDisposable> AcquireAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default);
}

public sealed class TenantStagingOperationConflictException()
    : InvalidOperationException("Another staging lifecycle operation is already in progress.");

