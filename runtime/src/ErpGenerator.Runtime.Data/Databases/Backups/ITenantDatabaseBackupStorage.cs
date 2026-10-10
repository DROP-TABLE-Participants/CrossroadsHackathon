using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

public interface ITenantDatabaseBackupStorage
{
    string Mode { get; }

    Task<TenantDatabaseBackupStorageUploadResult> UploadAsync(
        TenantDatabaseBackupStorageUploadRequest request,
        CancellationToken cancellationToken = default);

    Task DownloadAsync(
        TenantDatabaseBackupStorageReference reference,
        string targetFilePath,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        TenantDatabaseBackupStorageReference reference,
        CancellationToken cancellationToken = default);

    Task<TenantDatabaseBackupStorageObjectMetadata?> GetMetadataAsync(
        TenantDatabaseBackupStorageReference reference,
        CancellationToken cancellationToken = default);
}

public sealed record TenantDatabaseBackupStorageUploadRequest(
    ResolvedTenantEnvironment Tenant,
    string BackupId,
    string Reason,
    string SourceFilePath,
    DateTimeOffset CreatedAt);

public sealed record TenantDatabaseBackupStorageUploadResult(
    string StorageMode,
    string ObjectKey,
    string? Bucket,
    string? LocalPath,
    long ContentLength,
    string Sha256);

public sealed record TenantDatabaseBackupStorageReference(
    string StorageMode,
    string ObjectKey,
    string? Bucket);

public sealed record TenantDatabaseBackupStorageObjectMetadata(
    long ContentLength,
    string? Sha256);
