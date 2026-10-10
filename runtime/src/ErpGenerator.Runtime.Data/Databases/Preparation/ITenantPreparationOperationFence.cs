using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Preparation;

public interface ITenantPreparationOperationFence
{
    Task<IAsyncDisposable> AcquireAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default);
}

public sealed class TenantPreparationOperationConflictException()
    : InvalidOperationException("Another ERP preparation operation is already in progress.");

