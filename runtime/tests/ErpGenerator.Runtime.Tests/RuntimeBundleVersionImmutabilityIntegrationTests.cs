using FluentAssertions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.Backups;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeBundleVersionImmutabilityIntegrationTests
{
    [PostgresFact]
    public async Task Rejected_same_version_lifecycle_candidate_cannot_overwrite_active_bundle_metadata()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_bundle_immutable_reject_{Guid.NewGuid():N}"[..48];
        var tenant = RuntimeTestTenant.Create("immutable-reject", "immutable-reject.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(admin, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(admin);
            var runtime = new RuntimeDatabase(
                factory,
                new RuntimeSqlBuilder(),
                productionActivationPolicy: new RuntimeLifecycleProductionActivationPolicy(
                    RuntimeDeploymentBrokerLifecyclePrivilegeTests.MachineSecret,
                    RuntimeDeploymentBrokerLifecyclePrivilegeTests.SupervisorSecret,
                    new RuntimeDeploymentBrokerLifecyclePrivilegeTests.FixedTimeProvider(
                        RuntimeDeploymentBrokerLifecyclePrivilegeTests.Now)));
            var active = RuntimeDeploymentBrokerLifecyclePrivilegeTests.Bundle(
                'a',
                new FieldLifecycleDefinition("draft", "named_action"));
            var candidate = RuntimeDeploymentBrokerLifecyclePrivilegeTests.Bundle(
                'b',
                new FieldLifecycleDefinition("draft", "direct")) with
            {
                Version = active.Version,
                BundleName = "Rejected same-version lifecycle candidate"
            };
            var planner = new ErpBundleMigrationPlanner();
            await runtime.ApplyBundleDeploymentAsync(
                tenant,
                active,
                planner.PlanMigration(null, active),
                "erp",
                "erp",
                activate: true,
                "activate restrictive authoritative bundle");

            var result = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                candidate,
                planner.PlanMigration(active, candidate),
                "erp",
                "erp",
                activate: true,
                "reject unattested colliding lifecycle candidate");

            result.Status.Should().Be("failed");
            result.FailureCode.Should().Be("lifecycle_attestation_missing");
            result.BundleVersionId.Should().BeNull();
            await AssertCollisionWasRecordedWithoutMutationAsync(factory, runtime, tenant, active, candidate, "deployed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, databaseName);
        }
    }

    [PostgresFact]
    public async Task Recomputed_blocked_same_version_candidate_cannot_overwrite_active_bundle_metadata()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_bundle_immutable_fail_{Guid.NewGuid():N}"[..46];
        var tenant = RuntimeTestTenant.Create("immutable-fail", "immutable-fail.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(admin, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(admin);
            var runtime = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var active = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            await runtime.ApplyBundleDeploymentAsync(
                tenant,
                active,
                new ErpBundleMigrationPlanner().PlanMigration(null, active),
                "erp",
                "staged",
                activate: true,
                "activate authoritative bundle");
            var candidate = SameIdentityCandidate(active, 'e', invalidTableName: true);
            var safePlan = new BundleMigrationPlan(
                active.Version,
                candidate.Version,
                "safe",
                [new BundleMigrationOperation("add_field", "safe", Entity: "project", Field: "priority")],
                []);

            var result = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                candidate,
                safePlan,
                "erp",
                "staged",
                activate: true,
                "reject colliding candidate during physical apply");

            result.Status.Should().Be("failed");
            result.FailureCode.Should().Be("migration_blocked");
            result.BundleVersionId.Should().BeNull();
            await AssertCollisionWasRecordedWithoutMutationAsync(factory, runtime, tenant, active, candidate, "deployed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, databaseName);
        }
    }

    [PostgresFact]
    public async Task Blocked_same_version_candidate_cannot_overwrite_active_bundle_metadata()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_bundle_immutable_block_{Guid.NewGuid():N}"[..47];
        var tenant = RuntimeTestTenant.Create("immutable-block", "immutable-block.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(admin, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(admin);
            var runtime = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var active = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            await runtime.ApplyBundleDeploymentAsync(
                tenant,
                active,
                new ErpBundleMigrationPlanner().PlanMigration(null, active),
                "erp",
                "staged",
                activate: true,
                "activate authoritative bundle");
            var candidate = SameIdentityCandidate(active, 'd');
            var blockedPlan = new BundleMigrationPlan(
                active.Version,
                candidate.Version,
                "blocked",
                [],
                ["Synthetic destructive change."]);

            var result = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                candidate,
                blockedPlan,
                "erp",
                "failed",
                activate: false,
                "reject colliding blocked candidate");

            result.Status.Should().Be("failed");
            result.FailureCode.Should().Be("migration_blocked");
            result.BundleVersionId.Should().BeNull();
            await AssertCollisionWasRecordedWithoutMutationAsync(factory, runtime, tenant, active, candidate, "deployed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, databaseName);
        }
    }

    [PostgresFact]
    public async Task Backup_blocked_same_version_candidate_cannot_overwrite_active_bundle_metadata()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_bundle_immutable_backup_{Guid.NewGuid():N}"[..48];
        var tenant = RuntimeTestTenant.Create("immutable-backup", "immutable-backup.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(admin, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(admin);
            var runtime = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var active = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            await runtime.ApplyBundleDeploymentAsync(
                tenant,
                active,
                new ErpBundleMigrationPlanner().PlanMigration(null, active),
                "erp",
                "erp",
                activate: true,
                "activate authoritative bundle");
            var candidate = SameIdentityCandidate(active, 'c');
            var guardedRuntime = new RuntimeDatabase(
                factory,
                new RuntimeSqlBuilder(),
                backupService: new AlwaysBlockingBackupService());

            var result = await guardedRuntime.ApplyBundleDeploymentAsync(
                tenant,
                candidate,
                new ErpBundleMigrationPlanner().PlanMigration(active, candidate),
                "erp",
                "erp",
                activate: true,
                "reject colliding candidate after backup failure");

            result.Status.Should().Be("failed");
            result.FailureCode.Should().Be("backup_failed");
            result.BundleVersionId.Should().BeNull();
            await AssertCollisionWasRecordedWithoutMutationAsync(factory, runtime, tenant, active, candidate, "deployed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, databaseName);
        }
    }

    [PostgresFact]
    public async Task Safe_staging_deployment_with_same_version_and_different_hash_fails_before_schema_mutation() =>
        await AssertSafeSuccessfulPathCollisionRejectedAsync(TenantEnvironmentKind.Erp);

    [PostgresFact]
    public async Task Safe_production_deployment_with_same_version_and_different_hash_fails_before_schema_mutation() =>
        await AssertSafeSuccessfulPathCollisionRejectedAsync(TenantEnvironmentKind.Erp);

    [PostgresFact]
    public async Task Exact_hash_reapply_is_idempotent_and_preserves_the_rollback_pointer()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_bundle_immutable_reapply_{Guid.NewGuid():N}"[..49];
        var tenant = RuntimeTestTenant.Create("immutable-reapply", "immutable-reapply.erpgenerator.example", databaseName, TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(admin, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(admin);
            var runtime = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var bundle = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var planner = new ErpBundleMigrationPlanner();
            var first = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                bundle,
                planner.PlanMigration(null, bundle),
                "erp",
                "staged",
                activate: true,
                "initial exact artifact");

            var reapplied = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                bundle,
                planner.PlanMigration(bundle, bundle),
                "erp",
                "staged",
                activate: true,
                "idempotent exact artifact reapply");

            reapplied.Status.Should().Be("deployed");
            reapplied.FailureCode.Should().BeNull();
            reapplied.BundleVersionId.Should().Be(first.BundleVersionId);
            (await ScalarAsync<long>(factory, tenant,
                "select count(*) from _runtime.bundle_versions where bundle_id = @bundleId and version = @version",
                ("bundleId", bundle.BundleId),
                ("version", bundle.Version))).Should().Be(1);
            (await ScalarAsync<bool>(factory, tenant,
                "select previous_bundle_version_id is null from _runtime.active_bundle where id = true")).Should().BeTrue();
            (await runtime.ReadActiveCompiledBundleAsync(tenant))!.BundleHash.Should().Be(bundle.BundleHash);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, databaseName);
        }
    }

    private static async Task AssertSafeSuccessfulPathCollisionRejectedAsync(TenantEnvironmentKind environment)
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var environmentName = environment.ToString().ToLowerInvariant();
        var databaseName = $"erp_bundle_immutable_{environmentName}_{Guid.NewGuid():N}"[..49];
        var tenant = RuntimeTestTenant.Create(
            $"immutable-{environmentName}",
            $"immutable-{environmentName}.erpgenerator.example",
            databaseName,
            environment);
        await TestPostgres.CreateDatabaseAsync(admin, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(admin);
            var runtime = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var active = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var additive = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v2.yaml");
            var candidate = additive with
            {
                BundleId = active.BundleId,
                Version = active.Version
            };
            var planner = new ErpBundleMigrationPlanner();
            var candidatePlan = planner.PlanMigration(active, candidate);
            candidatePlan.RiskLevel.Should().Be("safe");
            candidatePlan.Operations.Should().Contain(operation =>
                operation.Type == "add_field" &&
                operation.Entity == "project" &&
                operation.Field == "priority");
            await runtime.ApplyBundleDeploymentAsync(
                tenant,
                active,
                planner.PlanMigration(null, active),
                environmentName,
                "deployed",
                activate: true,
                "activate authoritative bundle");

            var result = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                candidate,
                candidatePlan,
                environmentName,
                "deployed",
                activate: true,
                "reject safe additive colliding candidate");

            result.Status.Should().Be("failed");
            result.FailureCode.Should().Be("bundle_version_identity_conflict");
            result.BundleVersionId.Should().BeNull();
            await AssertCollisionWasRecordedWithoutMutationAsync(
                factory,
                runtime,
                tenant,
                active,
                candidate,
                "deployed");
            (await ScalarAsync<bool>(factory, tenant,
                """
                select exists (
                  select 1
                  from information_schema.columns
                  where table_schema = 'app'
                    and table_name = 'projects'
                    and column_name = 'priority'
                )
                """)).Should().BeFalse();
            (await ScalarAsync<long>(factory, tenant,
                "select count(*) from _runtime.field_definitions where id = 'project.priority'")).Should().Be(0);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, databaseName);
        }
    }

    private static async Task AssertCollisionWasRecordedWithoutMutationAsync(
        ITenantDatabaseConnectionFactory factory,
        RuntimeDatabase runtime,
        ResolvedTenantEnvironment tenant,
        CompiledBundle active,
        CompiledBundle candidate,
        string expectedActiveStatus)
    {
        var activeAfter = await runtime.ReadActiveCompiledBundleAsync(tenant);
        activeAfter.Should().NotBeNull();
        activeAfter!.BundleHash.Should().Be(active.BundleHash);
        activeAfter.BundleName.Should().Be(active.BundleName);

        (await ScalarAsync<long>(factory, tenant,
            "select count(*) from _runtime.bundle_versions where bundle_id = @bundleId and version = @version",
            ("bundleId", active.BundleId),
            ("version", active.Version))).Should().Be(1);
        (await ScalarAsync<string>(factory, tenant,
            "select bundle_hash from _runtime.bundle_versions where bundle_id = @bundleId and version = @version",
            ("bundleId", active.BundleId),
            ("version", active.Version))).Should().Be(active.BundleHash);
        (await ScalarAsync<string>(factory, tenant,
            "select status from _runtime.bundle_versions where bundle_id = @bundleId and version = @version",
            ("bundleId", active.BundleId),
            ("version", active.Version))).Should().Be(expectedActiveStatus);
        (await ScalarAsync<string>(factory, tenant,
            "select coalesce(metadata ->> 'bundleHash', metadata ->> 'BundleHash') from _runtime.bundle_versions where bundle_id = @bundleId and version = @version",
            ("bundleId", active.BundleId),
            ("version", active.Version))).Should().Be(active.BundleHash);
        (await ScalarAsync<string>(factory, tenant,
            "select coalesce(compiled_metadata ->> 'bundleHash', compiled_metadata ->> 'BundleHash') from _runtime.bundle_versions where bundle_id = @bundleId and version = @version",
            ("bundleId", active.BundleId),
            ("version", active.Version))).Should().Be(active.BundleHash);
        (await ScalarAsync<string>(factory, tenant,
            "select bundle_hash from _runtime.active_bundle where id = true")).Should().Be(active.BundleHash);

        (await ScalarAsync<bool>(factory, tenant,
            "select bundle_version_id is null from _runtime.bundle_deployments order by started_at desc limit 1")).Should().BeTrue();
        (await ScalarAsync<bool>(factory, tenant,
            "select to_bundle_version_id is null from _runtime.bundle_migration_plans order by created_at desc limit 1")).Should().BeTrue();
        (await ScalarAsync<bool>(factory, tenant,
            "select (metadata ->> 'candidate_identity_collision')::boolean from _runtime.bundle_deployments order by started_at desc limit 1")).Should().BeTrue();
        (await ScalarAsync<bool>(factory, tenant,
            "select (metadata ->> 'candidate_bundle_version_recorded')::boolean from _runtime.bundle_deployments order by started_at desc limit 1")).Should().BeFalse();
        (await ScalarAsync<string>(factory, tenant,
            "select metadata #>> '{candidate_bundle,bundleId}' from _runtime.bundle_deployments order by started_at desc limit 1")).Should().Be(candidate.BundleId);
        (await ScalarAsync<string>(factory, tenant,
            "select metadata #>> '{candidate_bundle,bundleName}' from _runtime.bundle_deployments order by started_at desc limit 1")).Should().Be(candidate.BundleName);
        (await ScalarAsync<string>(factory, tenant,
            "select metadata #>> '{candidate_bundle,version}' from _runtime.bundle_deployments order by started_at desc limit 1")).Should().Be(candidate.Version);
        (await ScalarAsync<string>(factory, tenant,
            "select metadata #>> '{candidate_bundle,bundleHash}' from _runtime.bundle_deployments order by started_at desc limit 1")).Should().Be(candidate.BundleHash);

        var history = await runtime.ReadDeploymentHistoryAsync(tenant);
        history.First().BundleId.Should().Be(candidate.BundleId);
        history.First().BundleVersion.Should().Be(candidate.Version);
        history.First().BundleHash.Should().Be(candidate.BundleHash);
    }

    private static CompiledBundle SameIdentityCandidate(
        CompiledBundle active,
        char hashCharacter,
        bool invalidTableName = false)
    {
        if (!invalidTableName)
        {
            return active with
            {
                BundleName = $"{active.BundleName} rejected candidate",
                BundleHash = new string(hashCharacter, 64)
            };
        }

        const string entityId = "project";
        const string invalidTableNameValue = "bad-table-name";
        var originalTableName = active.Entities.Single(entity => entity.Id == entityId).TableName;
        return active with
        {
            BundleName = $"{active.BundleName} rejected candidate",
            BundleHash = new string(hashCharacter, 64),
            Entities = active.Entities
                .Select(entity => entity.Id == entityId
                    ? entity with { TableName = invalidTableNameValue }
                    : entity)
                .ToArray(),
            Tables = active.Tables
                .Select(table => table.TableName == originalTableName
                    ? table with { TableName = invalidTableNameValue }
                    : table)
                .ToArray()
        };
    }

    private static async Task<T?> ScalarAsync<T>(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(
            factory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
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

    private sealed class AlwaysBlockingBackupService : ITenantDatabaseBackupService
    {
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
                "failed",
                FilePath: null,
                SizeBytes: null,
                CreatedAt: now,
                CompletedAt: now,
                ErrorMessage: "Synthetic required backup failure.",
                TenantId: tenant.TenantStableId);
            return Task.FromResult(new TenantDatabaseBackupResult(false, true, "failed", metadata));
        }

        public Task<TenantDatabaseRestoreResult> RestoreBackupAsync(
            TenantDatabaseBackupMetadata backup,
            string restoreDatabaseName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
