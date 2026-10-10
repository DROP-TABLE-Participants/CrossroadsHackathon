using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed class DisabledTenantDatabaseBackupService : ITenantDatabaseBackupService
{
    public static DisabledTenantDatabaseBackupService Instance { get; } = new();

    public Task<TenantDatabaseBackupResult> CreateBackupAsync(
        ResolvedTenantEnvironment tenant,
        string reason,
        bool required,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new TenantDatabaseBackupMetadata(
            Guid.NewGuid().ToString("N"),
            tenant.Environment.ToString().ToLowerInvariant(),
            tenant.DatabaseName,
            reason,
            required ? "failed" : "skipped",
            null,
            null,
            now,
            now,
            required ? "Tenant database backups are disabled." : null);

        return Task.FromResult(new TenantDatabaseBackupResult(!required, required, metadata.Status, metadata));
    }

    public Task<TenantDatabaseRestoreResult> RestoreBackupAsync(
        TenantDatabaseBackupMetadata backup,
        string restoreDatabaseName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TenantDatabaseRestoreResult(false, "failed", restoreDatabaseName, "Tenant database backups are disabled."));
}
