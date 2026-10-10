using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

public interface ITenantDatabaseBackupService
{
    Task<TenantDatabaseBackupResult> CreateBackupAsync(
        ResolvedTenantEnvironment tenant,
        string reason,
        bool required,
        CancellationToken cancellationToken = default);

    Task<TenantDatabaseRestoreResult> RestoreBackupAsync(
        TenantDatabaseBackupMetadata backup,
        string restoreDatabaseName,
        CancellationToken cancellationToken = default);
}
