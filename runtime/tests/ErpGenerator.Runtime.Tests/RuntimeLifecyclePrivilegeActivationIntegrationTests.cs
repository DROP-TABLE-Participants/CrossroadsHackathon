using FluentAssertions;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeLifecyclePrivilegeActivationIntegrationTests
{
    [PostgresFact]
    public async Task Direct_database_production_activation_cannot_bypass_with_staging_strings()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_lifecycle_guard_{Guid.NewGuid():N}"[..45];
        await TestPostgres.CreateDatabaseAsync(admin, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create(
                "lifecycle-guard",
                "lifecycle-guard.erpgenerator.test",
                databaseName,
                TenantEnvironmentKind.Production);
            var runtime = RuntimeDatabase(admin);
            var planner = new ErpBundleMigrationPlanner();
            var active = RuntimeDeploymentBrokerLifecyclePrivilegeTests.Bundle(
                'a',
                new FieldLifecycleDefinition("draft", "named_action"));
            var candidate = RuntimeDeploymentBrokerLifecyclePrivilegeTests.Bundle(
                'b',
                new FieldLifecycleDefinition("draft", "direct"));
            await runtime.ApplyBundleDeploymentAsync(
                tenant,
                active,
                planner.PlanMigration(null, active),
                "production",
                "production",
                activate: true,
                "initial restrictive bundle");

            var blocked = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                candidate,
                planner.PlanMigration(active, candidate),
                environment: "staging",
                status: "staged",
                activate: true,
                message: "attempt caller-string bypass");

            blocked.Status.Should().Be("failed");
            blocked.FailureCode.Should().Be("lifecycle_attestation_missing");
            (await runtime.ReadActiveCompiledBundleAsync(tenant))!.BundleHash.Should().Be(active.BundleHash);
            var staleAuthorization = Authorization(
                tenant,
                active with { BundleHash = new string('c', 64) },
                candidate,
                "apply-stale-base",
                "machine-stale-base",
                "review-stale-base");
            var stale = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                candidate,
                planner.PlanMigration(active, candidate),
                "production",
                "production",
                true,
                "stale signed base",
                productionAuthorization: staleAuthorization);
            stale.FailureCode.Should().Be("lifecycle_attestation_lineage_mismatch");
            (await runtime.ReadActiveCompiledBundleAsync(tenant))!.BundleHash.Should().Be(active.BundleHash);
            (await runtime.ReadDeploymentHistoryAsync(tenant)).Should().Contain(item =>
                item.Status == "failed" && item.Environment == "production");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, databaseName);
        }
    }

    [PostgresFact]
    public async Task Exact_evidence_is_consumed_with_activation_and_cannot_replay_after_rollback()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_lifecycle_replay_{Guid.NewGuid():N}"[..45];
        await TestPostgres.CreateDatabaseAsync(admin, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create(
                "lifecycle-replay",
                "lifecycle-replay.erpgenerator.test",
                databaseName,
                TenantEnvironmentKind.Production);
            var runtime = RuntimeDatabase(admin);
            var planner = new ErpBundleMigrationPlanner();
            var active = RuntimeDeploymentBrokerLifecyclePrivilegeTests.Bundle(
                'a',
                new FieldLifecycleDefinition("draft", "named_action"));
            var candidate = RuntimeDeploymentBrokerLifecyclePrivilegeTests.Bundle(
                'b',
                new FieldLifecycleDefinition("draft", "direct"));
            await runtime.ApplyBundleDeploymentAsync(
                tenant,
                active,
                planner.PlanMigration(null, active),
                "production",
                "production",
                true,
                "initial restrictive bundle");
            var authorization = Authorization(tenant, active, candidate, "apply-replay", "machine-replay", "review-replay");

            var promoted = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                candidate,
                planner.PlanMigration(active, candidate),
                "production",
                "production",
                true,
                "authorized widening",
                productionAuthorization: authorization);
            promoted.Status.Should().Be("production");

            await runtime.RollbackActiveBundleAsync(tenant);
            (await runtime.ReadActiveCompiledBundleAsync(tenant))!.BundleHash.Should().Be(active.BundleHash);

            var replay = await runtime.ApplyBundleDeploymentAsync(
                tenant,
                candidate,
                planner.PlanMigration(active, candidate),
                "production",
                "production",
                true,
                "replayed widening",
                productionAuthorization: authorization);

            replay.Status.Should().Be("failed");
            replay.FailureCode.Should().Be("lifecycle_attestation_replayed");
            (await runtime.ReadActiveCompiledBundleAsync(tenant))!.BundleHash.Should().Be(active.BundleHash);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, databaseName);
        }
    }

    [PostgresFact]
    public async Task Legacy_rollback_cannot_reactivate_an_older_wider_bundle_without_authorization()
    {
        var admin = TestPostgres.GetRequiredAdminConnectionString();
        var databaseName = $"erp_lifecycle_rollback_{Guid.NewGuid():N}"[..45];
        await TestPostgres.CreateDatabaseAsync(admin, databaseName);
        try
        {
            var tenant = RuntimeTestTenant.Create(
                "lifecycle-rollback",
                "lifecycle-rollback.erpgenerator.test",
                databaseName,
                TenantEnvironmentKind.Production);
            var runtime = RuntimeDatabase(admin);
            var planner = new ErpBundleMigrationPlanner();
            var wider = RuntimeDeploymentBrokerLifecyclePrivilegeTests.Bundle(
                'a',
                new FieldLifecycleDefinition("draft", "direct"));
            var restrictive = RuntimeDeploymentBrokerLifecyclePrivilegeTests.Bundle(
                'b',
                new FieldLifecycleDefinition("draft", "named_action"));
            var firstAuthorization = Authorization(
                tenant,
                active: null,
                wider,
                "apply-initial-direct",
                "machine-initial-direct",
                "review-initial-direct");
            await runtime.ApplyBundleDeploymentAsync(
                tenant,
                wider,
                planner.PlanMigration(null, wider),
                "production",
                "production",
                true,
                "authorized first direct bundle",
                productionAuthorization: firstAuthorization);
            await runtime.ApplyBundleDeploymentAsync(
                tenant,
                restrictive,
                planner.PlanMigration(wider, restrictive),
                "production",
                "production",
                true,
                "restrict lifecycle");

            var act = async () => await runtime.RollbackActiveBundleAsync(tenant);

            await act.Should().ThrowAsync<RuntimeLifecyclePrivilegePromotionRejectedException>()
                .Where(exception => exception.FailureCode == "lifecycle_attestation_missing");
            (await runtime.ReadActiveCompiledBundleAsync(tenant))!.BundleHash.Should().Be(restrictive.BundleHash);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(admin, databaseName);
        }
    }

    private static RuntimeDatabase RuntimeDatabase(string adminConnectionString) =>
        new(
            TestPostgres.CreateConnectionFactory(adminConnectionString),
            new RuntimeSqlBuilder(),
            productionActivationPolicy: new RuntimeLifecycleProductionActivationPolicy(
                RuntimeDeploymentBrokerLifecyclePrivilegeTests.MachineSecret,
                RuntimeDeploymentBrokerLifecyclePrivilegeTests.SupervisorSecret,
                new RuntimeDeploymentBrokerLifecyclePrivilegeTests.FixedTimeProvider(
                    RuntimeDeploymentBrokerLifecyclePrivilegeTests.Now)));

    private static RuntimeLifecyclePrivilegePromotionAuthorization Authorization(
        ResolvedTenantEnvironment tenant,
        CompiledBundle? active,
        CompiledBundle candidate,
        string applyIdentity,
        string attestationId,
        string reviewId)
    {
        var productionIdentity = RuntimeLifecyclePrivilegePromotionAuthorization.ComputeEnvironmentIdentity(
            tenant.TenantStableId,
            "production",
            tenant.DatabaseName);
        var envelope = new RuntimeLifecyclePrivilegePromotionAuthorization(
            $"operation-{applyIdentity}",
            "run-integration",
            "project-integration",
            tenant.TenantStableId,
            RuntimeLifecyclePrivilegePromotionAuthorization.ComputeEnvironmentIdentity(
                tenant.TenantStableId,
                "staging",
                $"{tenant.DatabaseName}_staging"),
            productionIdentity,
            new string('d', 64),
            applyIdentity,
            active?.BundleHash ?? RuntimeLifecyclePrivilegePromotionRules.NoActiveProductionBundleHash,
            candidate.BundleHash);
        using var authority = new RuntimeLifecyclePrivilegeAttestationAuthority(
            RuntimeDeploymentBrokerLifecyclePrivilegeTests.MachineSecret,
            RuntimeDeploymentBrokerLifecyclePrivilegeTests.SupervisorSecret,
            new RuntimeDeploymentBrokerLifecyclePrivilegeTests.FixedTimeProvider(
                RuntimeDeploymentBrokerLifecyclePrivilegeTests.Now));
        var machine = authority.RecordMachineAttestation(
            attestationId,
            envelope,
            RuntimeLifecyclePrivilegeMachineStatus.Passed,
            RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate),
            RuntimeDeploymentBrokerLifecyclePrivilegeTests.Now);
        var supervisor = authority.RecordSupervisorAttestation(
            reviewId,
            machine,
            new string('e', 64),
            RuntimeLifecyclePrivilegeSupervisorDecision.Approve,
            RuntimeDeploymentBrokerLifecyclePrivilegeTests.Now);
        return envelope with
        {
            MachineAttestation = machine,
            SupervisorAttestation = supervisor
        };
    }
}
