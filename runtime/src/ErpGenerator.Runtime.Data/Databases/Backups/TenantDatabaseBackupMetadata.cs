namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed record TenantDatabaseBackupMetadata(
    string BackupId,
    string Environment,
    string DatabaseName,
    string Reason,
    string Status,
    string? FilePath,
    long? SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage,
    string? TenantId = null,
    string? StorageMode = null,
    string? StorageObjectKey = null,
    string? StorageBucket = null,
    long? ContentLength = null,
    string? Sha256 = null,
    string? CreatedByOperation = null,
    string? RestoreStatus = null,
    string? RestoreTargetDatabase = null,
    DateTimeOffset? RestoredAt = null,
    string? RestoreErrorMessage = null);
