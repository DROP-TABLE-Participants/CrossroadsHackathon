using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeSeedValidationIntegrationTests
{
    [PostgresFact]
    public async Task Seed_runner_wraps_record_validation_with_safe_record_context()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_seed_validation_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "seed-validation",
                "seed-validation.erpgenerator.example",
                database,
                TenantEnvironmentKind.Erp);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var secretRawRelationValue = "raw-relation-value-do-not-leak";
            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "invalid-demo", Label = "Invalid demo" },
                Records =
                [
                    new BundleSeedRecord
                    {
                        Ref = "project-invalid-customer",
                        Entity = "project",
                        Values = new Dictionary<string, object?>
                        {
                            ["title"] = "Invalid project",
                            ["customer"] = secretRawRelationValue
                        }
                    }
                ]
            };
            var runner = new RuntimeSeedRunner(new RuntimeRecordStore(factory));

            var action = () => runner.ApplySeedAsync(
                tenant,
                new RuntimeUserContext(0, "runtime-test@erpgenerator.local", "_runtime_owner"),
                seed);

            var thrown = await action.Should().ThrowAsync<RuntimeSeedRecordValidationException>();
            thrown.Which.SeedRef.Should().Be("project-invalid-customer");
            thrown.Which.Entity.Should().Be("project");
            thrown.Which.Fields.Should().Equal("customer_id");
            thrown.Which.ToString().Should().NotContain(secretRawRelationValue);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }
}
