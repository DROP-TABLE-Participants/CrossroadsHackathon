namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed class TenantDatabaseBackupOptions
{
    public TenantDatabaseBackupMode Mode { get; init; } = TenantDatabaseBackupMode.Disabled;

    public string AdminConnectionString { get; init; } = string.Empty;

    public TenantDatabaseBackupStorageMode StorageMode { get; init; } = TenantDatabaseBackupStorageMode.LocalFile;

    public string LocalDirectory { get; init; } = ".tmp/erpgenerator-backups";

    public string TempDirectory { get; init; } = string.Empty;

    public string PgDumpPath { get; init; } = "pg_dump";

    public string PgRestorePath { get; init; } = "pg_restore";

    public string CreatedbPath { get; init; } = "createdb";

    public string DropdbPath { get; init; } = "dropdb";

    public bool RequireBeforePromotion { get; init; }

    public TenantDatabaseBackupS3Options S3 { get; init; } = new();
}

public sealed class TenantDatabaseBackupS3Options
{
    public string Bucket { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string AccessKeyId { get; init; } = string.Empty;

    public string SecretAccessKey { get; init; } = string.Empty;

    public bool ForcePathStyle { get; init; }

    public string Prefix { get; init; } = "tenant-db-backups/";

    public string ServerSideEncryption { get; init; } = string.Empty;
}
