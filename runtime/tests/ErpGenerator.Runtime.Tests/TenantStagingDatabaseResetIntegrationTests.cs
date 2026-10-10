using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.Staging;

namespace ErpGenerator.Runtime.Tests;

public sealed class TenantStagingDatabaseResetIntegrationTests
{
    [PostgresFact]
    public async Task Held_cross_instance_fence_rejects_reset_and_same_bundle_can_be_deployed_after_reset()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var stableId = Guid.NewGuid().ToString("N");
        var databaseName = TenantDatabaseNamePolicy.BuildExpected(stableId, TenantEnvironmentKind.Staging);
        var tenant = new ResolvedTenantEnvironment(
            "tenant-reset",
            stableId,
            "reset-test",
            "staging.reset-test.erpgenerator.example",
            TenantEnvironmentKind.Staging,
            databaseName,
            null,
            "https://staging.reset-test.erpgenerator.example");
        var options = Options.Create(new RuntimeDatabaseOptions
        {
            AdminConnectionString = adminConnectionString,
            AutoProvisionDatabases = true,
            CredentialMode = TenantDatabaseCredentialMode.Provisioned,
            ProvisionTenantAppUsers = true,
            TenantAppUserPrefix = "erp_reset_test"
        });
        var credentialStore = new InMemoryTenantDatabaseCredentialStore();
        var provisioner = new PostgresTenantDatabaseProvisioner(options, credentialStore);
        var fence = new PostgresTenantStagingOperationFence(options);
        var resetter = new PostgresTenantStagingDatabaseResetter(options, provisioner, credentialStore, fence);

        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);
        try
        {
            var factory = new TenantDatabaseConnectionFactory(options, credentialStore);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder(), provisioner);
            var compiled = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var planner = new ErpBundleMigrationPlanner();
            var first = await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                planner.PlanMigration(null, compiled),
                "staging",
                "staged",
                activate: true,
                "first staging deployment");
            first.Status.Should().Be("staged");

            await using (await fence.AcquireAsync(tenant))
            {
                var blocked = () => resetter.ResetAsync(tenant);
                await blocked.Should().ThrowAsync<TenantStagingOperationConflictException>()
                    .WithMessage("Another staging lifecycle operation is already in progress.");
            }

            var reset = await resetter.ResetAsync(tenant);
            reset.Status.Should().Be("reset");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant)).Should().BeNull();

            var second = await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                planner.PlanMigration(null, compiled),
                "staging",
                "staged",
                activate: true,
                "same bundle after reset");
            second.Status.Should().Be("staged");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant))!.BundleHash.Should().Be(compiled.BundleHash);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
            if (credentialStore.TryGet(tenant, out var credential))
            {
                await DropRoleAsync(adminConnectionString, credential.UserName);
            }
        }
    }

    private static async Task DropRoleAsync(string adminConnectionString, string roleName)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"drop role if exists \"{roleName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        await command.ExecuteNonQueryAsync();
    }
}
