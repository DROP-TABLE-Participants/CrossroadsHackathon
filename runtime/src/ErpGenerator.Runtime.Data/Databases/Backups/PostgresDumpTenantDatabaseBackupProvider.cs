using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed class PostgresDumpTenantDatabaseBackupProvider(
    IOptions<TenantDatabaseBackupOptions> backupOptions,
    ProcessRunner processRunner,
    ILogger<PostgresDumpTenantDatabaseBackupProvider>? logger = null)
{
    private readonly TenantDatabaseBackupOptions backupOptions = backupOptions.Value;

    public async Task<PostgresDumpResult> CreateDumpAsync(
        ResolvedTenantEnvironment tenant,
        string backupId,
        string reason,
        string filePath,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Path.GetTempPath());
        var connection = CreateBackupAdminConnection(tenant.DatabaseName);
        var host = RequiredConnectionPart(connection.Host, "Host");
        var userName = RequiredConnectionPart(connection.Username, "Username");
        var database = RequiredConnectionPart(connection.Database, "Database");

        var started = Stopwatch.GetTimestamp();
        logger?.LogInformation(
            "pg_dump backup {BackupId} started for tenant {TenantId} environment {Environment} database {DatabaseName}.",
            backupId,
            tenant.TenantStableId,
            tenant.Environment.ToString().ToLowerInvariant(),
            database);

        var result = await processRunner.RunAsync(
            backupOptions.PgDumpPath,
            [
                "--format=custom",
                "--file", filePath,
                "--host", host,
                "--port", connection.Port.ToString(),
                "--username", userName,
                "--dbname", database
            ],
            BuildPasswordEnvironment(connection),
            cancellationToken);

        if (!result.Success)
        {
            TryDelete(filePath);
            logger?.LogWarning(
                "pg_dump backup {BackupId} failed for tenant {TenantId} environment {Environment} database {DatabaseName}; duration {DurationMs:0.0} ms.",
                backupId,
                tenant.TenantStableId,
                tenant.Environment.ToString().ToLowerInvariant(),
                database,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new PostgresDumpResult(false, filePath, null, now, DateTimeOffset.UtcNow, SanitizeError(result.StandardError, connection));
        }

        var fileInfo = new FileInfo(filePath);
        logger?.LogInformation(
            "pg_dump backup {BackupId} completed for tenant {TenantId} environment {Environment} database {DatabaseName}; size {SizeBytes} bytes; duration {DurationMs:0.0} ms.",
            backupId,
            tenant.TenantStableId,
            tenant.Environment.ToString().ToLowerInvariant(),
            database,
            fileInfo.Exists ? fileInfo.Length : null,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new PostgresDumpResult(true, filePath, fileInfo.Exists ? fileInfo.Length : null, now, DateTimeOffset.UtcNow, null);
    }

    public async Task<TenantDatabaseRestoreResult> RestoreDumpAsync(
        TenantDatabaseBackupMetadata backup,
        string dumpFilePath,
        string restoreDatabaseName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dumpFilePath) || !File.Exists(dumpFilePath))
        {
            logger?.LogWarning(
                "Tenant database restore for backup {BackupId} failed because the backup file was not found; target database {RestoreDatabaseName}.",
                backup.BackupId,
                restoreDatabaseName);
            return new TenantDatabaseRestoreResult(false, "failed", restoreDatabaseName, "Backup file was not found.");
        }

        var adminConnection = CreateBackupAdminConnection("postgres");
        var adminHost = RequiredConnectionPart(adminConnection.Host, "Host");
        var adminUser = RequiredConnectionPart(adminConnection.Username, "Username");

        var started = Stopwatch.GetTimestamp();
        logger?.LogInformation(
            "Tenant database restore started for backup {BackupId}; target database {RestoreDatabaseName}.",
            backup.BackupId,
            restoreDatabaseName);

        var createResult = await processRunner.RunAsync(
            backupOptions.CreatedbPath,
            [
                "--host", adminHost,
                "--port", adminConnection.Port.ToString(),
                "--username", adminUser,
                restoreDatabaseName
            ],
            BuildPasswordEnvironment(adminConnection),
            cancellationToken);
        if (!createResult.Success)
        {
            logger?.LogWarning(
                "Tenant database restore for backup {BackupId} failed while creating target database {RestoreDatabaseName}; duration {DurationMs:0.0} ms.",
                backup.BackupId,
                restoreDatabaseName,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new TenantDatabaseRestoreResult(false, "failed", restoreDatabaseName, SanitizeError(createResult.StandardError, adminConnection));
        }

        var restoreConnection = new NpgsqlConnectionStringBuilder(adminConnection.ConnectionString)
        {
            Database = restoreDatabaseName
        };
        var restoreHost = RequiredConnectionPart(restoreConnection.Host, "Host");
        var restoreUser = RequiredConnectionPart(restoreConnection.Username, "Username");
        var restoreResult = await processRunner.RunAsync(
            backupOptions.PgRestorePath,
            [
                "--host", restoreHost,
                "--port", restoreConnection.Port.ToString(),
                "--username", restoreUser,
                "--dbname", restoreDatabaseName,
                "--clean",
                "--if-exists",
                dumpFilePath
            ],
            BuildPasswordEnvironment(restoreConnection),
            cancellationToken);

        if (restoreResult.Success)
        {
            var validationError = await ValidateRestoredDatabaseAsync(restoreConnection.ConnectionString, cancellationToken);
            if (validationError is not null)
            {
                logger?.LogWarning(
                    "Tenant database restore for backup {BackupId} failed validation after restoring target database {RestoreDatabaseName}; duration {DurationMs:0.0} ms.",
                    backup.BackupId,
                    restoreDatabaseName,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return new TenantDatabaseRestoreResult(false, "failed", restoreDatabaseName, validationError);
            }

            logger?.LogInformation(
                "Tenant database restore completed for backup {BackupId}; target database {RestoreDatabaseName}; duration {DurationMs:0.0} ms.",
                backup.BackupId,
                restoreDatabaseName,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new TenantDatabaseRestoreResult(true, "completed", restoreDatabaseName, null);
        }

        logger?.LogWarning(
            "Tenant database restore for backup {BackupId} failed while restoring target database {RestoreDatabaseName}; duration {DurationMs:0.0} ms.",
            backup.BackupId,
            restoreDatabaseName,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new TenantDatabaseRestoreResult(false, "failed", restoreDatabaseName, SanitizeError(restoreResult.StandardError, restoreConnection));
    }

    private static Dictionary<string, string?> BuildPasswordEnvironment(NpgsqlConnectionStringBuilder connection) =>
        string.IsNullOrWhiteSpace(connection.Password)
            ? []
            : new Dictionary<string, string?> { ["PGPASSWORD"] = connection.Password };

    private NpgsqlConnectionStringBuilder CreateBackupAdminConnection(string databaseName)
        => new(CreateBackupAdminConnectionString(databaseName));

    internal string CreateBackupAdminConnectionString(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(backupOptions.AdminConnectionString))
        {
            throw new InvalidOperationException("Runtime backup/restore admin connection is not configured.");
        }

        return PostgresConnectionString.Normalize(backupOptions.AdminConnectionString, databaseName);
    }

    private static string RequiredConnectionPart(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Database connection {name} is not configured.") : value;

    private static string SanitizeError(string value, NpgsqlConnectionStringBuilder connection)
    {
        var sanitized = value;
        if (!string.IsNullOrWhiteSpace(connection.Password))
        {
            sanitized = sanitized.Replace(connection.Password, "***", StringComparison.Ordinal);
        }

        sanitized = sanitized.Replace(connection.ConnectionString, "***", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(sanitized) ? "Database backup command failed." : sanitized.Trim();
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
            // Best effort cleanup only; the failure will be reported through backup metadata.
        }
    }

    private static async Task<string?> ValidateRestoredDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var runtimeSchemaExists = await ExistsAsync(
            connection,
            "select exists (select 1 from information_schema.schemata where schema_name = '_runtime')",
            cancellationToken);
        if (!runtimeSchemaExists)
        {
            return "Restored database validation failed: _runtime schema is missing.";
        }

        var appSchemaExists = await ExistsAsync(
            connection,
            "select exists (select 1 from information_schema.schemata where schema_name = 'app')",
            cancellationToken);
        if (!appSchemaExists)
        {
            return "Restored database validation failed: app schema is missing.";
        }

        var activeBundleCount = await ScalarAsync<long>(
            connection,
            "select count(*) from _runtime.active_bundle",
            cancellationToken);
        if (activeBundleCount <= 0)
        {
            return "Restored database validation failed: active bundle metadata is missing.";
        }

        var appTableCount = await ScalarAsync<long>(
            connection,
            "select count(*) from information_schema.tables where table_schema = 'app'",
            cancellationToken);
        if (appTableCount <= 0)
        {
            return "Restored database validation failed: app tables are missing.";
        }

        return null;
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken) =>
        (bool)(await ScalarAsync<bool>(connection, sql, cancellationToken));

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Database validation returned no result."));
    }
}

public sealed record PostgresDumpResult(
    bool Success,
    string FilePath,
    long? SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);
