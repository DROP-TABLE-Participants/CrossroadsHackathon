using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class TenantDatabaseCredentialIsolationTests
{
    [PostgresFact]
    public async Task Provisioned_app_user_can_run_runtime_operations_but_cannot_run_ddl()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_cred_{suffix}";
        var tenant = CreateTenant("cred", "cred.erpgenerator.example", databaseName, suffix, TenantEnvironmentKind.Staging);
        var context = CreateCredentialIsolationContext(adminConnectionString);

        try
        {
            await context.Provisioner.ProvisionAsync(tenant);
            context.Store.TryGet(tenant, out var credential).Should().BeTrue();

            await using (var connection = new NpgsqlConnection(context.Factory.CreateConnectionString(tenant)))
            {
                await connection.OpenAsync();
                connection.Database.Should().Be(databaseName);
            }

            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            await context.RuntimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                context.RuntimeDatabase,
                tenant,
                $"owner-{suffix}@example.test");
            var records = new RuntimeRecordStore(context.Factory);

            var metadata = await context.RuntimeDatabase.ReadMetadataAsync(tenant);
            metadata.Entities.Should().NotBeEmpty();

            var customer = await records.CreateRecordAsync(tenant, owner, "customers", RuntimeJson.JsonObject(
                ("company_name", $"Credential Customer {suffix}"),
                ("contact_email", $"credential-{suffix}@example.test"),
                ("status", "active")));
            customer["id"].Should().NotBeNull();

            var customers = await records.ListRecordsAsync(tenant, "customers", new RuntimeRecordQuery());
            customers.Records.Should().ContainSingle(record => (string)record["company_name"]! == $"Credential Customer {suffix}");
            (await records.ListAuditEventsAsync(tenant)).Should().Contain(@event => @event.EventType == "record.created");

            await AssertDdlDeniedAsync(context.Factory, tenant, "create table app.should_not_create(id uuid)");
            await AssertDdlDeniedAsync(context.Factory, tenant, "create schema evil");
            await AssertDdlDeniedAsync(context.Factory, tenant, "drop schema app");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
            if (context.Store.TryGet(tenant, out var credential))
            {
                await DropRoleAsync(adminConnectionString, credential.UserName);
            }
        }
    }

    [PostgresFact]
    public async Task Tenant_app_user_cannot_connect_to_another_tenant_database()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var acmeDatabase = $"erp_tenant_cred_acme_{suffix}";
        var betaDatabase = $"erp_tenant_cred_beta_{suffix}";
        var acme = CreateTenant("acme", "acme.erpgenerator.example", acmeDatabase, $"acme{suffix}", TenantEnvironmentKind.Production);
        var beta = CreateTenant("beta", "beta.erpgenerator.example", betaDatabase, $"beta{suffix}", TenantEnvironmentKind.Production);
        var context = CreateCredentialIsolationContext(adminConnectionString);

        try
        {
            await context.Provisioner.ProvisionAsync(acme);
            await context.Provisioner.ProvisionAsync(beta);
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            await context.RuntimeDatabase.ApplyCompiledBundleAsync(acme, compiled);
            await context.RuntimeDatabase.ApplyCompiledBundleAsync(beta, compiled);

            context.Store.TryGet(acme, out var acmeCredential).Should().BeTrue();
            var betaTarget = new NpgsqlConnectionStringBuilder(acmeCredential.ConnectionString)
            {
                Database = betaDatabase
            };

            var exception = await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
            {
                await using var connection = new NpgsqlConnection(betaTarget.ConnectionString);
                await connection.OpenAsync();
            });

            exception.Message.Should().NotContain(new NpgsqlConnectionStringBuilder(acmeCredential.ConnectionString).Password);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, acmeDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, betaDatabase);
            if (context.Store.TryGet(acme, out var acmeCredential))
            {
                await DropRoleAsync(adminConnectionString, acmeCredential.UserName);
            }

            if (context.Store.TryGet(beta, out var betaCredential))
            {
                await DropRoleAsync(adminConnectionString, betaCredential.UserName);
            }
        }
    }

    [PostgresFact]
    public async Task V3_acceptance_lifecycle_uses_provisioning_credential_while_business_data_uses_restricted_runtime_user()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_cred_accept_{suffix}";
        var tenant = CreateTenant(
            "cred-accept",
            "cred-accept.erpgenerator.example",
            databaseName,
            suffix,
            TenantEnvironmentKind.Staging);
        var context = CreateCredentialIsolationContext(adminConnectionString);

        try
        {
            await context.Provisioner.ProvisionAsync(tenant);
            context.Store.TryGet(tenant, out _).Should().BeTrue();
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            await context.RuntimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);
            var records = new RuntimeRecordStore(context.Factory);
            var runner = new RuntimeBundleTestRunner(
                context.Factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(context.Factory),
                new RuntimeActionStore(context.Factory, new RuntimePermissionService(context.Factory)),
                new RuntimeReportStore(context.Factory));
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "restricted_runtime_seed", Label = "Restricted runtime seed" },
                Records =
                [
                    new BundleSeedRecord
                    {
                        Ref = "customer",
                        Entity = "customer",
                        Values = new Dictionary<string, object?>
                        {
                            ["company_name"] = $"Restricted acceptance customer {suffix}",
                            ["contact_email"] = $"restricted-acceptance-{suffix}@example.test",
                            ["status"] = "active"
                        }
                    }
                ]
            };
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "restricted_credential_v3",
                    Label = "V3 lifecycle works with a restricted tenant app credential"
                },
                Setup = new BundleAcceptanceSetup
                {
                    Seed = seed.Seed.Key,
                    SeedHash = BundleSeedContractHasher.ComputeHash(seed)
                }
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            (await runner.HasPassingResultAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                result.TestSuiteHash)).Should().BeTrue();
            var customers = await records.ListRecordsAsync(
                tenant,
                "customer",
                new RuntimeRecordQuery(PageSize: 10));
            customers.Total.Should().Be(1);
            customers.Records.Single()["company_name"]
                .Should().Be($"Restricted acceptance customer {suffix}");
            await AssertDdlDeniedAsync(context.Factory, tenant, "create table app.runtime_must_stay_restricted(id bigint)");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
            if (context.Store.TryGet(tenant, out var credential))
            {
                await DropRoleAsync(adminConnectionString, credential.UserName);
            }
        }
    }

    private static CredentialIsolationContext CreateCredentialIsolationContext(string adminConnectionString)
    {
        var options = Options.Create(new RuntimeDatabaseOptions
        {
            AdminConnectionString = adminConnectionString,
            AutoProvisionDatabases = true,
            CredentialMode = TenantDatabaseCredentialMode.Provisioned,
            ProvisionTenantAppUsers = true,
            TenantAppUserPrefix = "erp_app_test"
        });
        var store = new InMemoryTenantDatabaseCredentialStore();
        var provisioner = new PostgresTenantDatabaseProvisioner(options, store);
        var factory = new TenantDatabaseConnectionFactory(options, store);
        var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder(), provisioner);
        return new CredentialIsolationContext(store, provisioner, factory, runtimeDatabase);
    }

    private static ResolvedTenantEnvironment CreateTenant(
        string slug,
        string host,
        string databaseName,
        string stableIdSuffix,
        TenantEnvironmentKind environment) => new(
        slug,
        $"{slug}-{stableIdSuffix}",
        slug,
        host,
        environment,
        databaseName,
        null,
        $"http://{host}");

    private static async Task AssertDdlDeniedAsync(
        ITenantDatabaseConnectionFactory factory,
        ResolvedTenantEnvironment tenant,
        string sql)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var connection = new NpgsqlConnection(factory.CreateConnectionString(tenant));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        });
        exception.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static async Task DropRoleAsync(string adminConnectionString, string userName)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString.Normalize(adminConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"drop role if exists {QuoteIdentifier(userName)}";
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record CredentialIsolationContext(
        InMemoryTenantDatabaseCredentialStore Store,
        PostgresTenantDatabaseProvisioner Provisioner,
        TenantDatabaseConnectionFactory Factory,
        RuntimeDatabase RuntimeDatabase);
}
