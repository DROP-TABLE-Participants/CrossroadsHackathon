using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.Backups;
using Xunit.Abstractions;

namespace ErpGenerator.Runtime.Tests;

public sealed class TenantDatabaseBackupRestoreTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LocalFile_storage_uploads_downloads_verifies_existence_and_checksum()
    {
        var root = RuntimeBundleTestHelper.CreateTempDirectory();
        var source = Path.Combine(root, "source.dump");
        var target = Path.Combine(root, "target.dump");
        await File.WriteAllTextAsync(source, "tenant backup content");
        var options = Options.Create(new TenantDatabaseBackupOptions
        {
            StorageMode = TenantDatabaseBackupStorageMode.LocalFile,
            LocalDirectory = Path.Combine(root, "storage")
        });
        var storage = new LocalFileTenantDatabaseBackupStorage(options);
        var tenant = RuntimeTestTenant.Create("backup-storage", "backup-storage.erpgenerator.example", "erp_tenant_storage", TenantEnvironmentKind.Erp);

        try
        {
            var uploaded = await storage.UploadAsync(new TenantDatabaseBackupStorageUploadRequest(
                tenant,
                Guid.NewGuid().ToString("N"),
                "unit-test",
                source,
                DateTimeOffset.UtcNow));
            var reference = new TenantDatabaseBackupStorageReference(uploaded.StorageMode, uploaded.ObjectKey, uploaded.Bucket);

            uploaded.StorageMode.Should().Be(TenantDatabaseBackupStorageMode.LocalFile.ToString());
            uploaded.ObjectKey.Should().NotBeNullOrWhiteSpace();
            uploaded.ContentLength.Should().BeGreaterThan(0);
            uploaded.Sha256.Should().HaveLength(64);
            (await storage.ExistsAsync(reference)).Should().BeTrue();
            (await storage.GetMetadataAsync(reference))!.Sha256.Should().Be(uploaded.Sha256);

            await storage.DownloadAsync(reference, target);
            (await TenantDatabaseBackupChecksum.Sha256FileAsync(target, CancellationToken.None)).Should().Be(uploaded.Sha256);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Backup_provider_uses_only_the_scoped_backup_admin_connection()
    {
        var backupOptions = Options.Create(new TenantDatabaseBackupOptions
        {
            AdminConnectionString = "Host=backup-db.internal;Port=5433;Database=postgres;Username=backup_admin;Password=backup-password"
        });
        var provider = new PostgresDumpTenantDatabaseBackupProvider(
            backupOptions,
            new ProcessRunner());

        var connection = new NpgsqlConnectionStringBuilder(
            provider.CreateBackupAdminConnectionString("erp_tenant_scoped_prod"));

        connection.Host.Should().Be("backup-db.internal");
        connection.Port.Should().Be(5433);
        connection.Database.Should().Be("erp_tenant_scoped_prod");
        connection.Username.Should().Be("backup_admin");
    }

    [Fact]
    public void Backup_provider_does_not_fallback_when_scoped_backup_authority_is_missing()
    {
        var provider = new PostgresDumpTenantDatabaseBackupProvider(
            Options.Create(new TenantDatabaseBackupOptions()),
            new ProcessRunner());

        var act = () => provider.CreateBackupAdminConnectionString("erp_tenant_missing_prod");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*backup/restore admin connection is not configured*");
    }

    [PostgresFact]
    public async Task Backup_service_records_metadata_with_only_the_scoped_backup_authority()
    {
        var backupAdminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_backup_authority_{suffix}";
        var tenant = RuntimeTestTenant.Create(
            "backup-authority",
            "backup-authority.erpgenerator.example",
            databaseName,
            TenantEnvironmentKind.Erp);
        var backupDirectory = RuntimeBundleTestHelper.CreateTempDirectory();
        await TestPostgres.CreateDatabaseAsync(backupAdminConnectionString, databaseName);

        try
        {
            var backupService = CreateBackupService(
                backupAdminConnectionString,
                backupDirectory,
                TenantDatabaseBackupMode.Required,
                pgDumpPath: "pg_dump_erpgenerator_missing");

            var result = await backupService.CreateBackupAsync(tenant, "authority-test", required: true);

            result.Success.Should().BeFalse();
            result.Metadata.Status.Should().Be("failed");

            await using var connection = new NpgsqlConnection(
                PostgresConnectionString.Normalize(backupAdminConnectionString, databaseName));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "select backup_status from _runtime.tenant_database_backups where id = @id",
                connection);
            command.Parameters.AddWithValue("id", Guid.Parse(result.Metadata.BackupId));
            (await command.ExecuteScalarAsync()).Should().Be("failed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(backupAdminConnectionString, databaseName);
            TryDeleteDirectory(backupDirectory);
        }
    }

    [PostgresFact]
    public async Task Restore_rehearsal_creates_backup_restores_and_validates_metadata_and_rows()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        if (!BackupToolsAvailable())
        {
            output.WriteLine("Skipping backup/restore roundtrip because local pg_dump/pg_restore/createdb tools are unavailable.");
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_backup_{suffix}";
        var restoreDatabaseName = $"erp_restore_backup_{suffix}";
        var tenant = RuntimeTestTenant.Create("backup", "backup.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        var backupDirectory = RuntimeBundleTestHelper.CreateTempDirectory();
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, await RuntimeBundleTestHelper.CompileSampleBundleAsync());

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, tenant, $"owner-{suffix}@example.test");
            var records = new RuntimeRecordStore(factory);
            await records.CreateRecordAsync(tenant, owner, "customers", RuntimeJson.JsonObject(
                ("company_name", $"Backup Customer {suffix}"),
                ("contact_email", $"backup-{suffix}@example.test"),
                ("status", "active")));

            var backupService = CreateBackupService(adminConnectionString, backupDirectory, mode: TenantDatabaseBackupMode.Required);
            var backup = await backupService.CreateBackupAsync(tenant, "test-backup", required: true);

            backup.Success.Should().BeTrue(backup.Metadata.ErrorMessage);
            backup.Metadata.Status.Should().Be("completed");
            backup.Metadata.FilePath.Should().NotBeNullOrWhiteSpace();
            File.Exists(backup.Metadata.FilePath!).Should().BeTrue();
            backup.Metadata.SizeBytes.Should().BeGreaterThan(0);
            backup.Metadata.StorageMode.Should().Be(TenantDatabaseBackupStorageMode.LocalFile.ToString());
            backup.Metadata.StorageObjectKey.Should().NotBeNullOrWhiteSpace();
            backup.Metadata.ContentLength.Should().BeGreaterThan(0);
            backup.Metadata.Sha256.Should().HaveLength(64);
            (await ReadBackupStatusAsync(factory, tenant, backup.Metadata.BackupId)).Should().Be("completed");
            (await ReadBackupColumnAsync<string>(factory, tenant, backup.Metadata.BackupId, "storage_mode")).Should().Be(TenantDatabaseBackupStorageMode.LocalFile.ToString());
            (await ReadBackupColumnAsync<string>(factory, tenant, backup.Metadata.BackupId, "storage_object_key")).Should().NotBeNullOrWhiteSpace();
            (await ReadBackupColumnAsync<long>(factory, tenant, backup.Metadata.BackupId, "content_length")).Should().BeGreaterThan(0);
            (await ReadBackupColumnAsync<string>(factory, tenant, backup.Metadata.BackupId, "sha256")).Should().HaveLength(64);

            var restore = await backupService.RestoreBackupAsync(backup.Metadata, restoreDatabaseName);
            restore.Success.Should().BeTrue();

            await using var restoredConnection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString, restoreDatabaseName));
            await restoredConnection.OpenAsync();
            (await TableExistsAsync(restoredConnection, "_runtime", "bundle_versions")).Should().BeTrue();
            (await ScalarAsync<long>(restoredConnection, "select count(*) from _runtime.active_bundle")).Should().Be(1);
            (await TableExistsAsync(restoredConnection, "app", "customers")).Should().BeTrue();
            (await ScalarAsync<long>(restoredConnection, "select count(*) from app.customers")).Should().Be(1);
            (await ReadBackupColumnAsync<string>(factory, tenant, backup.Metadata.BackupId, "restore_status")).Should().Be("completed");
            (await ReadBackupColumnAsync<string>(factory, tenant, backup.Metadata.BackupId, "restore_target_database")).Should().Be(restoreDatabaseName);
        }
        finally
        {
            if (ShouldKeepRestoreRehearsalDatabases())
            {
                output.WriteLine($"Keeping restore rehearsal databases {databaseName} and {restoreDatabaseName} because KEEP_RESTORE_REHEARSAL_DBS=true.");
            }
            else
            {
                await TestPostgres.DropDatabaseAsync(adminConnectionString, restoreDatabaseName);
                await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
            }

            TryDeleteDirectory(backupDirectory);
        }
    }

    [Fact]
    public async Task Restore_refuses_in_place_target_database()
    {
        var backupDirectory = RuntimeBundleTestHelper.CreateTempDirectory();
        try
        {
            var backupService = CreateBackupService(
                "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres",
                backupDirectory,
                TenantDatabaseBackupMode.Required);
            var metadata = new TenantDatabaseBackupMetadata(
                Guid.NewGuid().ToString("N"),
                "erp",
                "erp_tenant_same_database",
                "restore-test",
                "completed",
                FilePath: null,
                SizeBytes: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                CompletedAt: DateTimeOffset.UtcNow,
                ErrorMessage: null,
                TenantId: "restore-test",
                StorageMode: TenantDatabaseBackupStorageMode.LocalFile.ToString(),
                StorageObjectKey: "restore-test.dump",
                StorageBucket: null,
                ContentLength: 1,
                Sha256: new string('a', 64));

            var restore = await backupService.RestoreBackupAsync(metadata, metadata.DatabaseName);

            restore.Success.Should().BeFalse();
            restore.ErrorMessage.Should().Contain("new database");
        }
        finally
        {
            TryDeleteDirectory(backupDirectory);
        }
    }

    [PostgresFact]
    public async Task Required_pre_promotion_backup_failure_blocks_promotion_and_records_failure()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_backup_guard_{suffix}";
        var tenant = RuntimeTestTenant.Create("backup-guard", "backup-guard.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        var backupDirectory = RuntimeBundleTestHelper.CreateTempDirectory();
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var planner = new ErpBundleMigrationPlanner();
            var v1 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var v2 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml");
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await runtimeDatabase.ApplyBundleDeploymentAsync(tenant, v1, planner.PlanMigration(null, v1), "erp", "erp", activate: true, "v1 promoted");

            var backupService = CreateBackupService(
                adminConnectionString,
                backupDirectory,
                TenantDatabaseBackupMode.Required,
                pgDumpPath: "pg_dump_erpgenerator_missing");
            var guardedRuntimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder(), backupService: backupService);
            var v2Plan = planner.PlanMigration(await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant), v2);

            var result = await guardedRuntimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v2,
                v2Plan,
                "erp",
                "erp",
                activate: true,
                "v2 promoted");

            result.Status.Should().Be("failed");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.Version.Should().Be(v1.Version);
            (await ReadLatestDeploymentStatusAsync(factory, tenant)).Should().Be("failed");
            (await ReadLatestBackupStatusAsync(factory, tenant)).Should().Be("failed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
            TryDeleteDirectory(backupDirectory);
        }
    }

    [PostgresFact]
    public async Task Best_effort_backup_failure_does_not_block_promotion_when_not_required()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_backup_best_{suffix}";
        var tenant = RuntimeTestTenant.Create("backup-best", "backup-best.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        var backupDirectory = RuntimeBundleTestHelper.CreateTempDirectory();
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var planner = new ErpBundleMigrationPlanner();
            var v1 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var v2 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml");
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await runtimeDatabase.ApplyBundleDeploymentAsync(tenant, v1, planner.PlanMigration(null, v1), "erp", "erp", activate: true, "v1 promoted");

            var backupService = CreateBackupService(
                adminConnectionString,
                backupDirectory,
                TenantDatabaseBackupMode.BestEffort,
                requireBeforePromotion: false,
                pgDumpPath: "pg_dump_erpgenerator_missing");
            var guardedRuntimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder(), backupService: backupService);
            var v2Plan = planner.PlanMigration(await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant), v2);

            var result = await guardedRuntimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v2,
                v2Plan,
                "erp",
                "erp",
                activate: true,
                "v2 promoted");

            result.Status.Should().Be("deployed");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.Version.Should().Be(v2.Version);
            (await ReadLatestBackupStatusAsync(factory, tenant)).Should().Be("failed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
            TryDeleteDirectory(backupDirectory);
        }
    }

    [PostgresFact]
    public async Task Backup_failure_error_does_not_include_connection_password()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_backup_mask_{suffix}";
        var tenant = RuntimeTestTenant.Create("backup-mask", "backup-mask.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        var backupDirectory = RuntimeBundleTestHelper.CreateTempDirectory();
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, await RuntimeBundleTestHelper.CompileSampleBundleAsync());

            var backupService = CreateBackupService(
                adminConnectionString,
                backupDirectory,
                TenantDatabaseBackupMode.Required,
                pgDumpPath: "pg_dump_erpgenerator_missing");
            var backup = await backupService.CreateBackupAsync(tenant, "test-mask", required: true);
            var password = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize(adminConnectionString)).Password;

            backup.Success.Should().BeFalse();
            if (!string.IsNullOrWhiteSpace(password))
            {
                backup.Metadata.ErrorMessage.Should().NotContain(password);
            }
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
            TryDeleteDirectory(backupDirectory);
        }
    }

    [PostgresFact]
    public async Task Backup_storage_error_does_not_include_object_storage_keys()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        if (!BackupToolsAvailable())
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_backup_s3mask_{suffix}";
        var tenant = RuntimeTestTenant.Create("backup-s3-mask", "backup-s3-mask.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        var backupDirectory = RuntimeBundleTestHelper.CreateTempDirectory();
        const string accessKey = "backup-access-key-mask-test";
        const string secretKey = "backup-secret-access-key-mask-test";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, await RuntimeBundleTestHelper.CompileSampleBundleAsync());

            var backupService = CreateBackupService(
                adminConnectionString,
                backupDirectory,
                TenantDatabaseBackupMode.Required,
                storage: new ThrowingBackupStorage(accessKey, secretKey),
                accessKey: accessKey,
                secretKey: secretKey);
            var backup = await backupService.CreateBackupAsync(tenant, "test-s3-mask", required: true);

            backup.Success.Should().BeFalse();
            backup.Metadata.ErrorMessage.Should().NotContain(accessKey);
            backup.Metadata.ErrorMessage.Should().NotContain(secretKey);
            backup.Metadata.ErrorMessage.Should().Contain("***");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
            TryDeleteDirectory(backupDirectory);
        }
    }


    private static TenantDatabaseBackupService CreateBackupService(
        string adminConnectionString,
        string backupDirectory,
        TenantDatabaseBackupMode mode,
        bool requireBeforePromotion = true,
        string pgDumpPath = "pg_dump",
        ITenantDatabaseBackupStorage? storage = null,
        string accessKey = "",
        string secretKey = "")
    {
        var backupOptions = Options.Create(new TenantDatabaseBackupOptions
        {
            Mode = mode,
            AdminConnectionString = adminConnectionString,
            RequireBeforePromotion = requireBeforePromotion,
            LocalDirectory = backupDirectory,
            PgDumpPath = pgDumpPath,
            PgRestorePath = "pg_restore",
            CreatedbPath = "createdb",
            DropdbPath = "dropdb",
            S3 = new TenantDatabaseBackupS3Options
            {
                AccessKeyId = accessKey,
                SecretAccessKey = secretKey
            }
        });
        storage ??= new LocalFileTenantDatabaseBackupStorage(backupOptions);
        var provider = new PostgresDumpTenantDatabaseBackupProvider(backupOptions, new ProcessRunner());
        return new TenantDatabaseBackupService(backupOptions, provider, storage);
    }

    private static bool BackupToolsAvailable() =>
        ToolExists("pg_dump") && ToolExists("pg_restore") && ToolExists("createdb");

    private static bool ToolExists(string executable)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Any(path => File.Exists(Path.Combine(path, executable)));
    }

    private static async Task<string?> ReadBackupStatusAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string backupId)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select backup_status from _runtime.tenant_database_backups where id = @id";
        command.Parameters.AddWithValue("id", Guid.Parse(backupId));
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<T?> ReadBackupColumnAsync<T>(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string backupId,
        string columnName)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"select {columnName} from _runtime.tenant_database_backups where id = @id";
        command.Parameters.AddWithValue("id", Guid.Parse(backupId));
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task<string?> ReadLatestBackupStatusAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select backup_status from _runtime.tenant_database_backups order by created_at desc limit 1";
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string?> ReadLatestDeploymentStatusAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select status from _runtime.bundle_deployments order by started_at desc limit 1";
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string schemaName, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select exists (
              select 1
              from information_schema.tables
              where table_schema = @schemaName and table_name = @tableName
            )
            """;
        command.Parameters.AddWithValue("schemaName", schemaName);
        command.Parameters.AddWithValue("tableName", tableName);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Query returned no result."));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private static bool ShouldKeepRestoreRehearsalDatabases() =>
        string.Equals(Environment.GetEnvironmentVariable("KEEP_RESTORE_REHEARSAL_DBS"), "true", StringComparison.OrdinalIgnoreCase);

    private sealed class ThrowingBackupStorage(string accessKey, string secretKey) : ITenantDatabaseBackupStorage
    {
        public string Mode => TenantDatabaseBackupStorageMode.S3Compatible.ToString();

        public Task<TenantDatabaseBackupStorageUploadResult> UploadAsync(
            TenantDatabaseBackupStorageUploadRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"Object storage failed for {accessKey} and {secretKey}.");

        public Task DownloadAsync(
            TenantDatabaseBackupStorageReference reference,
            string targetFilePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used.");

        public Task<bool> ExistsAsync(
            TenantDatabaseBackupStorageReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<TenantDatabaseBackupStorageObjectMetadata?> GetMetadataAsync(
            TenantDatabaseBackupStorageReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TenantDatabaseBackupStorageObjectMetadata?>(null);
    }
}
