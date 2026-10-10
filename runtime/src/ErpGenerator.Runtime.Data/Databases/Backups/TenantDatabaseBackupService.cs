using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed class TenantDatabaseBackupService(
    IOptions<TenantDatabaseBackupOptions> options,
    PostgresDumpTenantDatabaseBackupProvider provider,
    ITenantDatabaseBackupStorage storage,
    ILogger<TenantDatabaseBackupService>? logger = null)
    : ITenantDatabaseBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TenantDatabaseBackupOptions options = options.Value;

    public async Task<TenantDatabaseBackupResult> CreateBackupAsync(
        ResolvedTenantEnvironment tenant,
        string reason,
        bool required,
        CancellationToken cancellationToken = default)
    {
        var effectiveRequired = required || options.RequireBeforePromotion || options.Mode == TenantDatabaseBackupMode.Required;
        var started = Stopwatch.GetTimestamp();
        if (options.Mode == TenantDatabaseBackupMode.Disabled)
        {
            var skipped = BuildMetadata(
                Guid.NewGuid().ToString("N"),
                tenant,
                reason,
                effectiveRequired ? "failed" : "skipped",
                filePath: null,
                storageMode: storage.Mode,
                objectKey: null,
                bucket: null,
                sizeBytes: null,
                contentLength: null,
                sha256: null,
                createdAt: DateTimeOffset.UtcNow,
                completedAt: DateTimeOffset.UtcNow,
                errorMessage: effectiveRequired ? "Tenant database backups are disabled." : null);
            await RecordBackupMetadataAsync(tenant, skipped, cancellationToken);
            logger?.LogWarning(
                "Tenant database backup {BackupId} {Status} for tenant {TenantId} environment {Environment} database {DatabaseName}; reason {Reason}; required {Required}; duration {DurationMs:0.0} ms.",
                skipped.BackupId,
                skipped.Status,
                tenant.TenantStableId,
                skipped.Environment,
                skipped.DatabaseName,
                reason,
                effectiveRequired,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new TenantDatabaseBackupResult(!effectiveRequired, effectiveRequired, skipped.Status, skipped);
        }

        var backupId = Guid.NewGuid().ToString("N");
        var tempFilePath = CreateTempDumpPath(backupId, "backup");
        logger?.LogInformation(
            "Tenant database backup {BackupId} started for tenant {TenantId} environment {Environment} database {DatabaseName}; reason {Reason}; required {Required}.",
            backupId,
            tenant.TenantStableId,
            tenant.Environment.ToString().ToLowerInvariant(),
            tenant.DatabaseName,
            reason,
            effectiveRequired);
        TenantDatabaseBackupMetadata metadata;
        try
        {
            var dump = await provider.CreateDumpAsync(tenant, backupId, reason, tempFilePath, cancellationToken);
            if (!dump.Success)
            {
                metadata = BuildMetadata(
                    backupId,
                    tenant,
                    reason,
                    "failed",
                    filePath: null,
                    storageMode: storage.Mode,
                    objectKey: null,
                    bucket: null,
                    sizeBytes: dump.SizeBytes,
                    contentLength: null,
                    sha256: null,
                    createdAt: dump.CreatedAt,
                    completedAt: dump.CompletedAt,
                    errorMessage: dump.ErrorMessage);
            }
            else
            {
                var uploaded = await storage.UploadAsync(
                    new TenantDatabaseBackupStorageUploadRequest(tenant, backupId, reason, dump.FilePath, dump.CreatedAt),
                    cancellationToken);
                var reference = new TenantDatabaseBackupStorageReference(
                    uploaded.StorageMode,
                    uploaded.ObjectKey,
                    uploaded.Bucket);
                var exists = await storage.ExistsAsync(reference, cancellationToken);
                var objectMetadata = exists
                    ? await storage.GetMetadataAsync(reference, cancellationToken)
                    : null;

                if (!exists || objectMetadata is null)
                {
                    metadata = BuildMetadata(
                        backupId,
                        tenant,
                        reason,
                        "failed",
                        uploaded.LocalPath,
                        uploaded.StorageMode,
                        uploaded.ObjectKey,
                        uploaded.Bucket,
                        dump.SizeBytes,
                        uploaded.ContentLength,
                        uploaded.Sha256,
                        dump.CreatedAt,
                        DateTimeOffset.UtcNow,
                        "Backup object was not found after upload.");
                }
                else
                {
                    metadata = BuildMetadata(
                        backupId,
                        tenant,
                        reason,
                        "completed",
                        uploaded.LocalPath,
                        uploaded.StorageMode,
                        uploaded.ObjectKey,
                        uploaded.Bucket,
                        dump.SizeBytes,
                        objectMetadata.ContentLength,
                        objectMetadata.Sha256 ?? uploaded.Sha256,
                        dump.CreatedAt,
                        DateTimeOffset.UtcNow,
                        errorMessage: null);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metadata = BuildMetadata(
                backupId,
                tenant,
                reason,
                "failed",
                filePath: null,
                storageMode: storage.Mode,
                objectKey: null,
                bucket: null,
                sizeBytes: null,
                contentLength: null,
                sha256: null,
                createdAt: DateTimeOffset.UtcNow,
                completedAt: DateTimeOffset.UtcNow,
                errorMessage: SanitizeError(ex.Message));
            logger?.LogError(
                "Tenant database backup {BackupId} failed before metadata recording for tenant {TenantId} environment {Environment} database {DatabaseName}; reason {Reason}.",
                backupId,
                tenant.TenantStableId,
                tenant.Environment.ToString().ToLowerInvariant(),
                tenant.DatabaseName,
                reason);
        }
        finally
        {
            TryDelete(tempFilePath);
        }

        await RecordBackupMetadataAsync(tenant, metadata, cancellationToken);

        var success = metadata.Status == "completed";
        if (success)
        {
            logger?.LogInformation(
                "Tenant database backup {BackupId} completed for tenant {TenantId} environment {Environment} database {DatabaseName}; size {SizeBytes} bytes; duration {DurationMs:0.0} ms.",
                metadata.BackupId,
                tenant.TenantStableId,
                metadata.Environment,
                metadata.DatabaseName,
                metadata.SizeBytes,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        else
        {
            logger?.LogWarning(
                "Tenant database backup {BackupId} failed for tenant {TenantId} environment {Environment} database {DatabaseName}; status {Status}; required {Required}; duration {DurationMs:0.0} ms.",
                metadata.BackupId,
                tenant.TenantStableId,
                metadata.Environment,
                metadata.DatabaseName,
                metadata.Status,
                effectiveRequired,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return new TenantDatabaseBackupResult(success || !effectiveRequired, effectiveRequired, metadata.Status, metadata);
    }

    public Task<TenantDatabaseRestoreResult> RestoreBackupAsync(
        TenantDatabaseBackupMetadata backup,
        string restoreDatabaseName,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(backup.DatabaseName, restoreDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new TenantDatabaseRestoreResult(
                false,
                "failed",
                restoreDatabaseName,
                "Restore target database must be a new database name."));
        }

        return RestoreBackupCoreAsync(backup, restoreDatabaseName, cancellationToken);
    }

    private static TenantDatabaseBackupMetadata BuildMetadata(
        string backupId,
        ResolvedTenantEnvironment tenant,
        string reason,
        string status,
        string? filePath,
        string? storageMode,
        string? objectKey,
        string? bucket,
        long? sizeBytes,
        long? contentLength,
        string? sha256,
        DateTimeOffset createdAt,
        DateTimeOffset? completedAt,
        string? errorMessage)
        => new(
            backupId,
            tenant.Environment.ToString().ToLowerInvariant(),
            tenant.DatabaseName,
            reason,
            status,
            filePath,
            sizeBytes,
            createdAt,
            completedAt,
            errorMessage,
            TenantId: tenant.TenantStableId,
            StorageMode: storageMode,
            StorageObjectKey: objectKey,
            StorageBucket: bucket,
            ContentLength: contentLength,
            Sha256: sha256,
            CreatedByOperation: reason);

    private async Task<TenantDatabaseRestoreResult> RestoreBackupCoreAsync(
        TenantDatabaseBackupMetadata backup,
        string restoreDatabaseName,
        CancellationToken cancellationToken)
    {
        var tempFilePath = CreateTempDumpPath(backup.BackupId, "restore");
        TenantDatabaseRestoreResult result;
        try
        {
            if (string.IsNullOrWhiteSpace(backup.StorageObjectKey))
            {
                if (string.IsNullOrWhiteSpace(backup.FilePath) || !File.Exists(backup.FilePath))
                {
                    result = new TenantDatabaseRestoreResult(false, "failed", restoreDatabaseName, "Backup object was not found.");
                }
                else
                {
                    result = await provider.RestoreDumpAsync(backup, backup.FilePath, restoreDatabaseName, cancellationToken);
                }
            }
            else
            {
                await storage.DownloadAsync(
                    new TenantDatabaseBackupStorageReference(
                        backup.StorageMode ?? storage.Mode,
                        backup.StorageObjectKey,
                        backup.StorageBucket),
                    tempFilePath,
                    cancellationToken);
                result = await provider.RestoreDumpAsync(backup, tempFilePath, restoreDatabaseName, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new TenantDatabaseRestoreResult(false, "failed", restoreDatabaseName, SanitizeError(ex.Message));
        }
        finally
        {
            TryDelete(tempFilePath);
        }

        await RecordRestoreMetadataAsync(backup, result, cancellationToken);
        return result;
    }

    private async Task RecordBackupMetadataAsync(
        ResolvedTenantEnvironment tenant,
        TenantDatabaseBackupMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(provider.CreateBackupAdminConnectionString(tenant.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureBackupMetadataTableAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.tenant_database_backups
              (id, tenant_id, environment, database_name, backup_reason, created_by_operation, backup_status, backup_path, storage_mode, storage_object_key, storage_bucket, size_bytes, content_length, sha256, created_at, completed_at, error_message, restore_status, restore_target_database, restored_at, restore_error_message, metadata)
            values
              (@id, @tenantId, @environment, @databaseName, @reason, @createdByOperation, @status, @path, @storageMode, @storageObjectKey, @storageBucket, @sizeBytes, @contentLength, @sha256, @createdAt, @completedAt, @errorMessage, @restoreStatus, @restoreTargetDatabase, @restoredAt, @restoreErrorMessage, cast(@metadata as jsonb))
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", Guid.Parse(metadata.BackupId));
        command.Parameters.AddWithValue("tenantId", string.IsNullOrWhiteSpace(metadata.TenantId) ? DBNull.Value : metadata.TenantId);
        command.Parameters.AddWithValue("environment", metadata.Environment);
        command.Parameters.AddWithValue("databaseName", metadata.DatabaseName);
        command.Parameters.AddWithValue("reason", metadata.Reason);
        command.Parameters.AddWithValue("createdByOperation", string.IsNullOrWhiteSpace(metadata.CreatedByOperation) ? metadata.Reason : metadata.CreatedByOperation);
        command.Parameters.AddWithValue("status", metadata.Status);
        command.Parameters.AddWithValue("path", (object?)metadata.FilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("storageMode", string.IsNullOrWhiteSpace(metadata.StorageMode) ? DBNull.Value : metadata.StorageMode);
        command.Parameters.AddWithValue("storageObjectKey", string.IsNullOrWhiteSpace(metadata.StorageObjectKey) ? DBNull.Value : metadata.StorageObjectKey);
        command.Parameters.AddWithValue("storageBucket", string.IsNullOrWhiteSpace(metadata.StorageBucket) ? DBNull.Value : metadata.StorageBucket);
        command.Parameters.AddWithValue("sizeBytes", metadata.SizeBytes is null ? DBNull.Value : metadata.SizeBytes.Value);
        command.Parameters.AddWithValue("contentLength", metadata.ContentLength is null ? DBNull.Value : metadata.ContentLength.Value);
        command.Parameters.AddWithValue("sha256", string.IsNullOrWhiteSpace(metadata.Sha256) ? DBNull.Value : metadata.Sha256);
        command.Parameters.AddWithValue("createdAt", metadata.CreatedAt);
        command.Parameters.AddWithValue("completedAt", metadata.CompletedAt is null ? DBNull.Value : metadata.CompletedAt.Value);
        command.Parameters.AddWithValue("errorMessage", string.IsNullOrWhiteSpace(metadata.ErrorMessage) ? DBNull.Value : metadata.ErrorMessage);
        command.Parameters.AddWithValue("restoreStatus", string.IsNullOrWhiteSpace(metadata.RestoreStatus) ? DBNull.Value : metadata.RestoreStatus);
        command.Parameters.AddWithValue("restoreTargetDatabase", string.IsNullOrWhiteSpace(metadata.RestoreTargetDatabase) ? DBNull.Value : metadata.RestoreTargetDatabase);
        command.Parameters.AddWithValue("restoredAt", metadata.RestoredAt is null ? DBNull.Value : metadata.RestoredAt.Value);
        command.Parameters.AddWithValue("restoreErrorMessage", string.IsNullOrWhiteSpace(metadata.RestoreErrorMessage) ? DBNull.Value : metadata.RestoreErrorMessage);
        command.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new
        {
            metadata.StorageMode,
            metadata.StorageObjectKey,
            metadata.ContentLength,
            metadata.Sha256,
            metadata.CreatedByOperation
        }, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RecordRestoreMetadataAsync(
        TenantDatabaseBackupMetadata backup,
        TenantDatabaseRestoreResult restore,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(provider.CreateBackupAdminConnectionString(backup.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureBackupMetadataTableAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update _runtime.tenant_database_backups
            set restore_status = @status,
                restore_target_database = @restoreTargetDatabase,
                restored_at = @restoredAt,
                restore_error_message = @restoreErrorMessage
            where id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", Guid.Parse(backup.BackupId));
        command.Parameters.AddWithValue("status", restore.Status);
        command.Parameters.AddWithValue("restoreTargetDatabase", restore.RestoreDatabaseName);
        command.Parameters.AddWithValue("restoredAt", restore.Success ? DateTimeOffset.UtcNow : DBNull.Value);
        command.Parameters.AddWithValue("restoreErrorMessage", string.IsNullOrWhiteSpace(restore.ErrorMessage) ? DBNull.Value : restore.ErrorMessage);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task EnsureBackupMetadataTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var statement in BackupMetadataStatements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static IReadOnlyList<string> BackupMetadataStatements { get; } =
    [
        "create schema if not exists _runtime",
        """
        create table if not exists _runtime.tenant_database_backups (
          id uuid primary key,
          tenant_id text,
          environment varchar(40) not null,
          database_name varchar(128) not null,
          backup_reason varchar(120) not null,
          created_by_operation varchar(120),
          backup_status varchar(40) not null,
          backup_path text,
          storage_mode varchar(40),
          storage_object_key text,
          storage_bucket text,
          size_bytes bigint,
          content_length bigint,
          sha256 varchar(128),
          created_at timestamptz not null default now(),
          completed_at timestamptz,
          error_message text,
          restore_status varchar(40),
          restore_target_database varchar(128),
          restored_at timestamptz,
          restore_error_message text,
          metadata jsonb not null default '{}'::jsonb
        )
        """,
        "alter table if exists _runtime.tenant_database_backups add column if not exists tenant_id text",
        "alter table if exists _runtime.tenant_database_backups add column if not exists created_by_operation varchar(120)",
        "alter table if exists _runtime.tenant_database_backups add column if not exists storage_mode varchar(40)",
        "alter table if exists _runtime.tenant_database_backups add column if not exists storage_object_key text",
        "alter table if exists _runtime.tenant_database_backups add column if not exists storage_bucket text",
        "alter table if exists _runtime.tenant_database_backups add column if not exists content_length bigint",
        "alter table if exists _runtime.tenant_database_backups add column if not exists sha256 varchar(128)",
        "alter table if exists _runtime.tenant_database_backups add column if not exists restore_status varchar(40)",
        "alter table if exists _runtime.tenant_database_backups add column if not exists restore_target_database varchar(128)",
        "alter table if exists _runtime.tenant_database_backups add column if not exists restored_at timestamptz",
        "alter table if exists _runtime.tenant_database_backups add column if not exists restore_error_message text"
    ];

    private string SanitizeError(string value)
    {
        var sanitized = string.IsNullOrWhiteSpace(value) ? "Tenant database backup failed." : value.Trim();
        foreach (var secret in new[]
                 {
                     options.S3.AccessKeyId,
                     options.S3.SecretAccessKey
                 })
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                sanitized = sanitized.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return sanitized;
    }

    private string CreateTempDumpPath(string backupId, string operation)
    {
        var directory = string.IsNullOrWhiteSpace(options.TempDirectory)
            ? Path.Combine(Path.GetTempPath(), "erpgenerator-tenant-db-backups")
            : Path.GetFullPath(options.TempDirectory);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{operation}_{TenantDatabaseBackupStorageKeys.SanitizePart(backupId)}_{Guid.NewGuid():N}.dump");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort temp cleanup only; backup metadata carries the actual result.
        }
    }
}
