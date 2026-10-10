using FluentAssertions;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.Backups;
using ErpGenerator.Runtime.Data.Databases.DeploymentLocks;
using ErpGenerator.Runtime.Shared.Deployment;

namespace ErpGenerator.Runtime.Tests;

public sealed class TenantDeploymentLockTests
{
    [PostgresFact]
    public async Task Lock_acquisition_blocks_second_operation_until_release()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var databaseName = $"erp_tenant_lock_{Guid.NewGuid():N}"[..40];
        var tenant = RuntimeTestTenant.Create("lock", "lock.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var locks = CreateLockService(factory);

            var first = await locks.TryAcquireAsync(tenant, "erp", "deploy");
            first.Acquired.Should().BeTrue();

            var second = await locks.TryAcquireAsync(tenant, "erp", "deploy");
            second.Acquired.Should().BeFalse();
            second.Message.Should().Be("Another deployment operation is already in progress for this tenant environment.");

            await locks.ReleaseAsync(first.Handle!);

            var third = await locks.TryAcquireAsync(tenant, "erp", "deploy");
            third.Acquired.Should().BeTrue();
            await locks.ReleaseAsync(third.Handle!);

            (await HeldLockCountAsync(factory, tenant)).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Expired_lock_is_marked_expired_and_replaced()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var databaseName = $"erp_tenant_lock_exp_{Guid.NewGuid():N}"[..44];
        var tenant = RuntimeTestTenant.Create("lock-expired", "lock-expired.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var locks = CreateLockService(factory);
            var stale = await locks.AcquireOrThrowAsync(tenant, "erp", "deploy");
            await ExpireLockAsync(factory, tenant, stale.LockId);

            var replacement = await locks.TryAcquireAsync(tenant, "erp", "deploy");

            replacement.Acquired.Should().BeTrue();
            (await LockStatusAsync(factory, tenant, stale.LockId)).Should().Be("expired");
            (await LockStatusAsync(factory, tenant, replacement.Handle!.LockId)).Should().Be("held");
            await locks.ReleaseAsync(replacement.Handle);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Concurrent_lock_attempts_allow_only_one_holder()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var databaseName = $"erp_tenant_lock_con_{Guid.NewGuid():N}"[..44];
        var tenant = RuntimeTestTenant.Create("lock-concurrent", "lock-concurrent.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var locks = CreateLockService(factory);

            var attempts = await Task.WhenAll(
                locks.TryAcquireAsync(tenant, "erp", "deploy"),
                locks.TryAcquireAsync(tenant, "erp", "deploy"));

            attempts.Count(attempt => attempt.Acquired).Should().Be(1);
            attempts.Count(attempt => !attempt.Acquired).Should().Be(1);

            var acquired = attempts.Single(attempt => attempt.Acquired).Handle!;
            await locks.ReleaseAsync(acquired);
            (await HeldLockCountAsync(factory, tenant)).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Authoritative_boundary_holds_the_lease_row_until_the_mutation_transaction_finishes()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_tenant_lock_fence_{Guid.NewGuid():N}"[..48];
        var tenant = RuntimeTestTenant.Create(
            "lock-fence",
            "lock-fence.erpgenerator.example",
            databaseName,
            TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        TenantDeploymentLockHandle? replacement = null;
        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var locks = CreateLockService(factory);
            var original = await locks.AcquireOrThrowAsync(tenant, "erp", "deploy");
            await using var connection = new NpgsqlConnection(
                factory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await TenantDeploymentLockService.EnsureHeldAsync(connection, transaction, original, CancellationToken.None);
            await using (var expire = new NpgsqlCommand(
                             "update _runtime.deployment_locks set expires_at = now() - interval '1 second' where id = @id",
                             connection,
                             transaction))
            {
                expire.Parameters.AddWithValue("id", original.LockId);
                (await expire.ExecuteNonQueryAsync()).Should().Be(1);
            }

            var replacementAttempt = locks.TryAcquireAsync(tenant, "erp", "deploy");
            await Task.Delay(200);
            replacementAttempt.IsCompleted.Should().BeFalse(
                "the exact lease row must remain locked through the authoritative transaction");

            await transaction.CommitAsync();
            var result = await replacementAttempt.WaitAsync(TimeSpan.FromSeconds(10));
            result.Acquired.Should().BeTrue();
            replacement = result.Handle;
            (await LockStatusAsync(factory, tenant, original.LockId)).Should().Be("expired");
        }
        finally
        {
            if (replacement is not null)
            {
                var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
                await CreateLockService(factory).ReleaseAsync(replacement);
            }

            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Locked_promotion_is_rejected_and_active_bundle_remains_unchanged()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var databaseName = $"erp_tenant_lock_prom_{Guid.NewGuid():N}"[..45];
        var tenant = RuntimeTestTenant.Create("lock-promote", "lock-promote.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var locks = CreateLockService(factory);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var planner = new ErpBundleMigrationPlanner();
            var v1 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var v2 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml");
            await runtimeDatabase.ApplyBundleDeploymentAsync(tenant, v1, planner.PlanMigration(null, v1), "erp", "erp", activate: true, "v1 promoted");

            var held = await locks.AcquireOrThrowAsync(tenant, "erp", "promote");
            var plan = planner.PlanMigration(await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant), v2);

            var act = async () => await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v2,
                plan,
                "erp",
                "erp",
                activate: true,
                "v2 promoted");

            await act.Should().ThrowAsync<TenantDeploymentLockException>()
                .WithMessage("Another deployment operation is already in progress for this tenant environment.");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.Version.Should().Be(v1.Version);

            await locks.ReleaseAsync(held);
            (await HeldLockCountAsync(factory, tenant)).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Production_activation_with_staging_strings_contends_on_the_authoritative_production_lock()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_tenant_lock_truth_{Guid.NewGuid():N}"[..45];
        var tenant = RuntimeTestTenant.Create(
            "lock-truth",
            "lock-truth.erpgenerator.example",
            databaseName,
            TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var locks = CreateLockService(factory);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var planner = new ErpBundleMigrationPlanner();
            var v1 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var v2 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml");
            await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v1,
                planner.PlanMigration(null, v1),
                "erp",
                "erp",
                true,
                "v1 promoted");
            var held = await locks.AcquireOrThrowAsync(tenant, "erp", "promote");

            var act = async () => await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v2,
                planner.PlanMigration(v1, v2),
                environment: "erp",
                status: "staged",
                activate: true,
                message: "must still use production lock");

            await act.Should().ThrowAsync<TenantDeploymentLockException>();
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.BundleHash.Should().Be(v1.BundleHash);
            await locks.ReleaseAsync(held);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Staging_activation_with_production_strings_contends_on_the_authoritative_staging_lock()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_tenant_lock_stg_truth_{Guid.NewGuid():N}"[..49];
        var tenant = RuntimeTestTenant.Create(
            "lock-staging-truth",
            "lock-staging-truth.erpgenerator.example",
            databaseName,
            TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var locks = CreateLockService(factory);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var planner = new ErpBundleMigrationPlanner();
            var v1 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var v2 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml");
            await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v1,
                planner.PlanMigration(null, v1),
                "erp",
                "staged",
                true,
                "v1 staged");
            var held = await locks.AcquireOrThrowAsync(tenant, "erp", "deploy");

            var act = async () => await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v2,
                planner.PlanMigration(v1, v2),
                environment: "erp",
                status: "erp",
                activate: true,
                message: "must still use staging lock");

            await act.Should().ThrowAsync<TenantDeploymentLockException>();
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.BundleHash.Should().Be(v1.BundleHash);
            await locks.ReleaseAsync(held);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Expired_worker_cannot_mutate_after_a_replacement_takes_the_lease()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_tenant_lock_lease_{Guid.NewGuid():N}"[..48];
        var tenant = RuntimeTestTenant.Create(
            "lock-lease",
            "lock-lease.erpgenerator.example",
            databaseName,
            TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        var backup = new PausingBackupService();
        TenantDeploymentLockHandle? replacement = null;
        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var planner = new ErpBundleMigrationPlanner();
            var v1 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var v2 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml");
            var bootstrap = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            await bootstrap.ApplyBundleDeploymentAsync(
                tenant,
                v1,
                planner.PlanMigration(null, v1),
                "erp",
                "erp",
                activate: true,
                "v1 promoted");

            var locks = CreateLockService(factory);
            var runtimeDatabase = new RuntimeDatabase(
                factory,
                new RuntimeSqlBuilder(),
                backupService: backup,
                deploymentLocks: locks);
            var firstAttempt = runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v2,
                planner.PlanMigration(v1, v2),
                "erp",
                "erp",
                activate: true,
                "v2 promoted");
            await backup.Started.WaitAsync(TimeSpan.FromSeconds(10));

            await ExpireHeldLockAsync(factory, tenant);
            replacement = await locks.AcquireOrThrowAsync(tenant, "erp", "promote");
            backup.Continue();

            var act = async () => await firstAttempt;
            await act.Should().ThrowAsync<TenantDeploymentLockException>()
                .WithMessage("*lock lease was lost*");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.BundleHash
                .Should().Be(v1.BundleHash);
        }
        finally
        {
            backup.Continue();
            if (replacement is not null)
            {
                var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
                await CreateLockService(factory).ReleaseAsync(replacement);
            }

            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Failed_staging_deployment_records_failure_releases_lock_and_does_not_stage_candidate()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var databaseName = $"erp_tenant_lock_stage_{Guid.NewGuid():N}"[..46];
        var tenant = RuntimeTestTenant.Create("lock-stage", "lock-stage.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var planner = new ErpBundleMigrationPlanner();
            var v1 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var v2 = ChangeEntityTableName(await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml"), "project", "bad-table-name");
            await runtimeDatabase.ApplyBundleDeploymentAsync(tenant, v1, planner.PlanMigration(null, v1), "erp", "staged", activate: true, "v1 staged");
            var plan = SafeV2Plan(v1, v2);

            var result = await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v2,
                plan,
                "erp",
                "staged",
                activate: true,
                "v2 staged");

            result.Status.Should().Be("failed");
            result.FailureCode.Should().Be("migration_blocked");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.Version.Should().Be(v1.Version);
            (await LatestDeploymentStatusAsync(factory, tenant)).Should().Be("failed");
            (await LatestMigrationPlanStatusAsync(factory, tenant)).Should().Be("blocked");
            (await BundleVersionStatusAsync(factory, tenant, v2.Version)).Should().Be("failed");
            (await HeldLockCountAsync(factory, tenant)).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Recomputed_blocked_promotion_records_truthful_plan_releases_lock_and_keeps_active_bundle()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var databaseName = $"erp_tenant_lock_fail_prom_{Guid.NewGuid():N}"[..51];
        var tenant = RuntimeTestTenant.Create("lock-fail-promote", "lock-fail-promote.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var planner = new ErpBundleMigrationPlanner();
            var v1 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var v2 = ChangeEntityTableName(await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml"), "project", "bad-table-name");
            await runtimeDatabase.ApplyBundleDeploymentAsync(tenant, v1, planner.PlanMigration(null, v1), "erp", "erp", activate: true, "v1 promoted");
            var plan = SafeV2Plan(v1, v2);

            var result = await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v2,
                plan,
                "erp",
                "erp",
                activate: true,
                "v2 promoted");

            result.Status.Should().Be("failed");
            result.FailureCode.Should().Be("migration_blocked");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.Version.Should().Be(v1.Version);
            (await LatestDeploymentStatusAsync(factory, tenant)).Should().Be("failed");
            (await LatestMigrationPlanStatusAsync(factory, tenant)).Should().Be("blocked");
            (await HeldLockCountAsync(factory, tenant)).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Failed_rollback_records_failure_releases_lock_and_keeps_active_bundle()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var databaseName = $"erp_tenant_lock_rb_{Guid.NewGuid():N}"[..42];
        var tenant = RuntimeTestTenant.Create("lock-rollback", "lock-rollback.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var v1 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var v2 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml");
            await runtimeDatabase.ApplyBundleDeploymentAsync(tenant, v1, new ErpBundleMigrationPlanner().PlanMigration(null, v1), "erp", "erp", activate: true, "v1 promoted");
            var actualPlan = new ErpBundleMigrationPlanner().PlanMigration(v1, v2);
            await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                v2,
                actualPlan,
                "erp",
                "erp",
                activate: true,
                "v2 promoted");
            await MarkLatestAppliedPlanNonRollbackableAsync(factory, tenant);

            var act = async () => await runtimeDatabase.RollbackActiveBundleAsync(tenant);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Current bundle was produced by a non-rollbackable migration plan.");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.Version.Should().Be(v2.Version);
            (await LatestDeploymentStatusAsync(factory, tenant)).Should().Be("failed");
            (await HeldLockCountAsync(factory, tenant)).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static TenantDeploymentLockService CreateLockService(ITenantDatabaseConnectionFactory factory) =>
        new(factory, Options.Create(new TenantDeploymentLockOptions { LockTtlMinutes = 15 }));

    private static async Task ExpireHeldLockAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant)
    {
        await using var connection = new NpgsqlConnection(
            factory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "update _runtime.deployment_locks set expires_at = now() - interval '1 second' where status = 'held'";
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private sealed class PausingBackupService : ITenantDatabaseBackupService
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public void Continue() => resume.TrySetResult();

        public async Task<TenantDatabaseBackupResult> CreateBackupAsync(
            ResolvedTenantEnvironment tenant,
            string reason,
            bool required,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await resume.Task.WaitAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var metadata = new TenantDatabaseBackupMetadata(
                Guid.NewGuid().ToString("N"),
                tenant.Environment.ToString().ToLowerInvariant(),
                tenant.DatabaseName,
                reason,
                "completed",
                FilePath: null,
                SizeBytes: null,
                CreatedAt: now,
                CompletedAt: now,
                ErrorMessage: null,
                TenantId: tenant.TenantStableId);
            return new TenantDatabaseBackupResult(true, false, "completed", metadata);
        }

        public Task<TenantDatabaseRestoreResult> RestoreBackupAsync(
            TenantDatabaseBackupMetadata backup,
            string restoreDatabaseName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static BundleMigrationPlan SafeV2Plan(CompiledBundle from, CompiledBundle to) => new(
        from.Version,
        to.Version,
        "safe",
        [new BundleMigrationOperation("add_field", "safe", Entity: "project", Field: "priority")],
        []);

    private static CompiledBundle ChangeEntityTableName(
        CompiledBundle bundle,
        string entityId,
        string tableName)
    {
        var originalTableName = bundle.Entities.Single(entity => entity.Id == entityId).TableName;
        var entities = bundle.Entities
            .Select(entity => entity.Id == entityId
                ? entity with { TableName = tableName }
                : entity)
            .ToArray();
        var tables = bundle.Tables
            .Select(table => table.TableName == originalTableName
                ? table with { TableName = tableName }
                : table)
            .ToArray();

        return bundle with
        {
            BundleHash = new string('f', 64),
            Entities = entities,
            Tables = tables
        };
    }

    private static async Task ExpireLockAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        Guid lockId)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "update _runtime.deployment_locks set expires_at = now() - interval '1 minute' where id = @id";
        command.Parameters.AddWithValue("id", lockId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> LockStatusAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        Guid lockId) =>
        await ScalarAsync<string?>(factory, tenant, "select status from _runtime.deployment_locks where id = @id", ("id", lockId));

    private static async Task<long> HeldLockCountAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant) =>
        await ScalarAsync<long>(factory, tenant, "select count(*) from _runtime.deployment_locks where status = 'held'");

    private static async Task<string?> LatestDeploymentStatusAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant) =>
        await ScalarAsync<string?>(factory, tenant, "select status from _runtime.bundle_deployments order by started_at desc limit 1");

    private static async Task<string?> LatestMigrationPlanStatusAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant) =>
        await ScalarAsync<string?>(factory, tenant, "select status from _runtime.bundle_migration_plans order by created_at desc limit 1");

    private static async Task<string?> BundleVersionStatusAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string version) =>
        await ScalarAsync<string?>(factory, tenant, "select status from _runtime.bundle_versions where version = @version", ("version", version));

    private static async Task MarkLatestAppliedPlanNonRollbackableAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant)
    {
        var operations = JsonSerializer.Serialize(
            new[]
            {
                new BundleMigrationOperation(
                    "persisted_nonrollbackable_operation",
                    "safe",
                    Entity: "project",
                    Field: "priority",
                    Rollbackable: false)
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using var connection = new NpgsqlConnection(
            factory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update _runtime.bundle_migration_plans
            set operations = cast(@operations as jsonb)
            where id = (
              select id
              from _runtime.bundle_migration_plans
              where status = 'applied'
              order by applied_at desc, created_at desc
              limit 1
            )
            """;
        command.Parameters.AddWithValue("operations", operations);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task<T?> ScalarAsync<T>(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}
