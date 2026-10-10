using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeTenantIsolationIntegrationTests
{
    [PostgresFact]
    public async Task Generated_runtime_isolates_records_archive_audit_and_sessions_by_tenant_database()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var acmeDatabase = $"erp_tenant_acme_{suffix}";
        var betaDatabase = $"erp_tenant_beta_{suffix}";

        await TestPostgres.CreateDatabaseAsync(adminConnectionString, acmeDatabase);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, betaDatabase);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();

            var acme = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", acmeDatabase);
            var beta = RuntimeTestTenant.Create("beta", "beta.erpgenerator.example", betaDatabase);

            await runtimeDatabase.ApplyCompiledBundleAsync(acme, compiled);
            await runtimeDatabase.ApplyCompiledBundleAsync(beta, compiled);

            var acmeUser = new RuntimeUserContext(1, "owner@acme.test", "owner");
            var betaUser = new RuntimeUserContext(1, "owner@beta.test", "owner");

            var acmeCustomer = await records.CreateRecordAsync(acme, acmeUser, "customers", RuntimeJson.JsonObject(
                ("company_name", "Acme Customer"),
                ("contact_email", "acme@example.test"),
                ("status", "active")));
            var betaCustomer = await records.CreateRecordAsync(beta, betaUser, "customers", RuntimeJson.JsonObject(
                ("company_name", "Beta Customer"),
                ("contact_email", "beta@example.test"),
                ("status", "active")));

            var acmeCustomers = await records.ListRecordsAsync(acme, "customers", new RuntimeRecordQuery());
            var betaCustomers = await records.ListRecordsAsync(beta, "customers", new RuntimeRecordQuery());
            acmeCustomers.Records.Should().ContainSingle(record => (string)record["company_name"]! == "Acme Customer");
            acmeCustomers.Records.Should().NotContain(record => (string)record["company_name"]! == "Beta Customer");
            betaCustomers.Records.Should().ContainSingle(record => (string)record["company_name"]! == "Beta Customer");

            var entity = (await records.GetEntityAsync(acme, "customers"))!;
            (await permissions.IsAllowedAsync(acme, acmeUser, "archive", entity)).Should().BeTrue();
            (await permissions.IsAllowedAsync(acme, new RuntimeUserContext(2, "readonly@acme.test", "readonly"), "create", entity)).Should().BeFalse();
            (await permissions.IsAllowedAsync(acme, new RuntimeUserContext(3, "unknown@acme.test", "missing"), "read", entity)).Should().BeFalse();

            await records.ArchiveRecordAsync(acme, acmeUser, "customers", Convert.ToInt64(acmeCustomer["id"]), "integration test");
            (await records.ListRecordsAsync(acme, "customers", new RuntimeRecordQuery())).Records.Should().BeEmpty();
            (await records.ListRecordsAsync(acme, "customers", new RuntimeRecordQuery(IncludeArchived: true))).Records.Should().ContainSingle();
            (await records.ListRecordsAsync(beta, "customers", new RuntimeRecordQuery())).Records.Should().ContainSingle(record => Convert.ToInt64(record["id"]) == Convert.ToInt64(betaCustomer["id"]));

            await records.RestoreRecordAsync(acme, acmeUser, "customers", Convert.ToInt64(acmeCustomer["id"]));
            (await records.ListRecordsAsync(acme, "customers", new RuntimeRecordQuery())).Records.Should().ContainSingle();

            var acmeEvents = await records.ListAuditEventsAsync(acme);
            var betaEvents = await records.ListAuditEventsAsync(beta);
            acmeEvents.Should().Contain(@event => @event.EventType == "record.archived");
            betaEvents.Should().NotContain(@event => @event.EventType == "record.archived");

            var sessionService = new RuntimeSessionService(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Runtime:SessionSecret"] = "integration-session-secret"
                })
                .Build());
            var token = sessionService.CreateSession(acme.TenantStableId, acme.Host, 1, "owner@acme.test", "owner");
            sessionService.ValidateSession(token, acme.TenantStableId, acme.Host).Should().NotBeNull();
            sessionService.ValidateSession(token, beta.TenantStableId, beta.Host).Should().BeNull();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, acmeDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, betaDatabase);
        }
    }
}
