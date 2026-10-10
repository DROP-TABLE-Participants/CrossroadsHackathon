using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeMultiEntityReportIntegrationTests
{
    [PostgresFact]
    public async Task Joined_reports_are_parameterized_tenant_isolated_permission_safe_and_executable()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var acmeDatabase = $"erp_join_report_acme_{suffix}";
        var betaDatabase = $"erp_join_report_beta_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, acmeDatabase);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, betaDatabase);

        try
        {
            var acme = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", acmeDatabase);
            var beta = RuntimeTestTenant.Create("beta", "beta.erpgenerator.example", betaDatabase);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var reports = new RuntimeReportStore(factory);
            var users = new RuntimeUserStore(factory);
            var permissions = new RuntimePermissionService(factory);
            var bundle = JoinedReportBundle();
            var compiled = new ErpBundleCompiler().Compile(bundle);
            await runtimeDatabase.ApplyCompiledBundleAsync(acme, compiled);
            await runtimeDatabase.ApplyCompiledBundleAsync(beta, compiled);

            var acmeOwner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, acme, $"owner-{suffix}@acme.test");
            var betaOwner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, beta, $"owner-{suffix}@beta.test");
            var sourceReader = await InviteAndActivateAsync(
                users,
                runtimeDatabase,
                acme,
                acmeOwner,
                $"source-reader-{suffix}@acme.test",
                "source_reader");

            await SeedAcmeAsync(acme, records, acmeOwner, suffix);
            await SeedBetaAsync(beta, records, betaOwner, suffix);

            var definition = await reports.GetReportAsync(acme, "sales_by_region");
            definition.Should().NotBeNull();
            definition!.Joins.Should().BeEquivalentTo(
            [
                new RuntimeReportJoin("customer", "source", "customer_id", "customer", "inner", 1),
                new RuntimeReportJoin("region", "customer", "region_id", "region", "left", 2)
            ], options => options.WithStrictOrdering());
            var source = (await records.GetEntityAsync(acme, definition.SourceEntity))!;
            var acmeResult = await reports.ExecuteReportAsync(acme, acmeOwner, definition, source, new RuntimeReportQuery());
            acmeResult.Rows.Should().ContainSingle();
            acmeResult.Rows.Single()["region.name"].Should().BeOfType<string>().Which.Should().StartWith("Europe West");
            acmeResult.Rows.Single()["total_amount"].Should().Be(180m);
            acmeResult.Rows.Single()["active_orders"].Should().Be(2L);

            var betaDefinition = (await reports.GetReportAsync(beta, "sales_by_region"))!;
            var betaResult = await reports.ExecuteReportAsync(
                beta,
                betaOwner,
                betaDefinition,
                (await records.GetEntityAsync(beta, betaDefinition.SourceEntity))!,
                new RuntimeReportQuery());
            betaResult.Rows.Should().ContainSingle();
            betaResult.Rows.Single()["total_amount"].Should().Be(900m);
            betaResult.Rows.Single()["total_amount"].Should().NotBe(acmeResult.Rows.Single()["total_amount"]);

            var singleDefinition = (await reports.GetReportAsync(acme, "orders_by_status"))!;
            singleDefinition.Joins.Should().BeEmpty();
            var singleResult = await reports.ExecuteReportAsync(
                acme,
                acmeOwner,
                singleDefinition,
                (await records.GetEntityAsync(acme, singleDefinition.SourceEntity))!,
                new RuntimeReportQuery());
            singleResult.Rows.Should().Contain(row => Convert.ToString(row["status"]) == "open" && Equals(row["total_amount"], 1280m));

            var metadata = await runtimeDatabase.ReadMetadataAsync(acme);
            metadata.Reports.Single(report => report.GetProperty("Key").GetString() == "sales_by_region")
                .GetProperty("Joins").GetArrayLength().Should().Be(2);
            compiled.BundleHash.Should().HaveLength(64);

            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, permissions),
                reports);
            var passingAcceptance = await runner.RunAsync(
                acme,
                compiled.Version,
                compiled.BundleHash,
                seed: null,
                [Acceptance("joined_report_executes", "analyst", includeMetricAssertion: true)]);
            passingAcceptance.Status.Should().Be("passed");

            var deniedAcceptance = await runner.RunAsync(
                acme,
                compiled.Version,
                compiled.BundleHash,
                seed: null,
                [Acceptance("joined_report_requires_every_entity", "source_reader", includeMetricAssertion: false)]);
            deniedAcceptance.Status.Should().Be("failed");
            deniedAcceptance.Results.Single().Failures.Should().Contain(failure =>
                failure.Contains("not allowed", StringComparison.OrdinalIgnoreCase) &&
                failure.Contains("customer", StringComparison.OrdinalIgnoreCase));

            var sessionSecret = $"joined-report-session-{suffix}";
            using var application = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Runtime:SessionSecret"] = sessionSecret,
                            ["RuntimeDatabase:TenantConnectionStringTemplate"] = adminConnectionString
                        });
                    });
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IControlPlaneClient>();
                        services.AddSingleton<IControlPlaneClient>(new MultiTenantControlPlaneClient(acme, beta));
                    });
                });
            using var client = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var ownerCookie = RuntimeHttpTestHelper.CreateSessionCookie(
                sessionSecret, acme.TenantStableId, acme.Host, acmeOwner.UserId, acmeOwner.Email, acmeOwner.Role);
            var sourceReaderCookie = RuntimeHttpTestHelper.CreateSessionCookie(
                sessionSecret, acme.TenantStableId, acme.Host, sourceReader.UserId, sourceReader.Email, sourceReader.Role);

            using var ownerRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Get, "/api/runtime/reports/sales_by_region", acme.Host, ownerCookie);
            var ownerResponse = await client.SendAsync(ownerRequest);
            ownerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var ownerJson = JsonDocument.Parse(await ownerResponse.Content.ReadAsStringAsync()))
            {
                ownerJson.RootElement.GetProperty("rows")[0].GetProperty("total_amount").GetDecimal().Should().Be(180m);
            }

            using var indexRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Get, "/api/runtime/reports", acme.Host, sourceReaderCookie);
            var indexResponse = await client.SendAsync(indexRequest);
            indexResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var indexJson = JsonDocument.Parse(await indexResponse.Content.ReadAsStringAsync()))
            {
                var keys = indexJson.RootElement.EnumerateArray().Select(item => item.GetProperty("key").GetString()).ToArray();
                keys.Should().Contain("orders_by_status");
                keys.Should().NotContain("sales_by_region");
            }

            using var deniedRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Get, "/api/runtime/reports/sales_by_region", acme.Host, sourceReaderCookie);
            (await client.SendAsync(deniedRequest)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var auditBoundary = (await records.ListAuditEventsAsync(acme, 200)).Max(@event => @event.Id);
            await CorruptJoinedMetricAfterCompilationAsync(factory.CreateConnectionString(acme));
            using var failClosedRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Get, "/api/runtime/reports/sales_by_region", acme.Host, sourceReaderCookie);
            (await client.SendAsync(failClosedRequest)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var afterDenied = (await records.ListAuditEventsAsync(acme, 200))
                .Where(@event => @event.Id > auditBoundary)
                .ToArray();
            afterDenied.Should().Contain(@event => @event.EventType == "report.permission_denied");
            afterDenied.Should().NotContain(@event => @event.EventType == "report.viewed");

            using var crossTenantRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                HttpMethod.Get, "/api/runtime/reports/sales_by_region", beta.Host, ownerCookie);
            (await client.SendAsync(crossTenantRequest)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, acmeDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, betaDatabase);
        }
    }

    private static BundleAcceptanceTestDocument Acceptance(string key, string role, bool includeMetricAssertion) => new()
    {
        SourcePath = $"{key}.test.yaml",
        Test = new BundleAcceptanceTest { Key = key, Label = key.Replace('_', ' ') },
        Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "call_joined_report",
                Type = "call_report",
                Report = "sales_by_region",
                AsRole = role
            }
        ],
        Assertions = includeMetricAssertion
            ?
            [
                new BundleAcceptanceAssertion
                {
                    Type = "report_metric_equals",
                    Report = "sales_by_region",
                    Metric = "total_amount",
                    Value = 180m
                }
            ]
            : []
    };

    private static async Task<RuntimeUserContext> InviteAndActivateAsync(
        RuntimeUserStore users,
        RuntimeDatabase runtimeDatabase,
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        RuntimeUserContext owner,
        string email,
        string role)
    {
        var invite = await users.InviteUserAsync(tenant, owner, email, role, [role], null, TimeSpan.FromDays(1));
        await users.ActivateInvitedUserAsync(tenant, invite.ActivationToken!, email, role, "MemberPass123!");
        var login = await runtimeDatabase.ValidateLoginAsync(tenant, email, "MemberPass123!");
        return new RuntimeUserContext(login!.Id, login.Email, login.Role) { Roles = login.Roles };
    }

    private static async Task SeedAcmeAsync(
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        RuntimeRecordStore records,
        RuntimeUserContext owner,
        string suffix)
    {
        var eu = await CreateRegionAsync(tenant, records, owner, $"Europe West {suffix}", "EU'WEST");
        var us = await CreateRegionAsync(tenant, records, owner, $"United States {suffix}", "US");
        var archivedRegion = await CreateRegionAsync(tenant, records, owner, $"Archived {suffix}", "EU'WEST");

        var active = await CreateCustomerAsync(tenant, records, owner, "Active", "active", eu);
        var inactive = await CreateCustomerAsync(tenant, records, owner, "Inactive", "inactive", eu);
        var usCustomer = await CreateCustomerAsync(tenant, records, owner, "US", "active", us);
        var archivedRegionCustomer = await CreateCustomerAsync(tenant, records, owner, "Archived region", "active", archivedRegion);
        await records.ArchiveRecordAsync(tenant, owner, "regions", Id(archivedRegion), "archived joined region");
        var archivedCustomer = await CreateCustomerAsync(tenant, records, owner, "Archived customer", "active", eu);

        await CreateOrderAsync(tenant, records, owner, "A-100", "open", 100m, active);
        await CreateOrderAsync(tenant, records, owner, "A-050", "open", 50m, active);
        await CreateOrderAsync(tenant, records, owner, "I-030", "open", 30m, inactive);
        await CreateOrderAsync(tenant, records, owner, "US-200", "open", 200m, usCustomer);
        await CreateOrderAsync(tenant, records, owner, "AR-500", "open", 500m, archivedRegionCustomer);
        await CreateOrderAsync(tenant, records, owner, "AC-400", "open", 400m, archivedCustomer);
        await records.ArchiveRecordAsync(tenant, owner, "customers", Id(archivedCustomer), "archived joined customer");
        await CreateOrderAsync(tenant, records, owner, "C-999", "closed", 999m, active);
        var archivedOrder = await CreateOrderAsync(tenant, records, owner, "O-600", "open", 600m, active);
        await records.ArchiveRecordAsync(tenant, owner, "orders", Id(archivedOrder), "archived source order");
    }

    private static async Task SeedBetaAsync(
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        RuntimeRecordStore records,
        RuntimeUserContext owner,
        string suffix)
    {
        var eu = await CreateRegionAsync(tenant, records, owner, $"Europe West {suffix}", "EU'WEST");
        var customer = await CreateCustomerAsync(tenant, records, owner, "Beta", "active", eu);
        await CreateOrderAsync(tenant, records, owner, "B-900", "open", 900m, customer);
    }

    private static Task<Dictionary<string, object?>> CreateRegionAsync(
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        RuntimeRecordStore records,
        RuntimeUserContext owner,
        string name,
        string code) =>
        records.CreateRecordAsync(tenant, owner, "regions", RuntimeJson.JsonObject(("name", name), ("code", code)));

    private static Task<Dictionary<string, object?>> CreateCustomerAsync(
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        RuntimeRecordStore records,
        RuntimeUserContext owner,
        string name,
        string status,
        IReadOnlyDictionary<string, object?> region) =>
        records.CreateRecordAsync(
            tenant,
            owner,
            "customers",
            RuntimeJson.JsonObject(("name", name), ("status", status), ("region_id", region["id"]!)));

    private static Task<Dictionary<string, object?>> CreateOrderAsync(
        ErpGenerator.Runtime.Shared.Tenants.ResolvedTenantEnvironment tenant,
        RuntimeRecordStore records,
        RuntimeUserContext owner,
        string number,
        string status,
        decimal amount,
        IReadOnlyDictionary<string, object?> customer) =>
        records.CreateRecordAsync(
            tenant,
            owner,
            "orders",
            RuntimeJson.JsonObject(
                ("order_number", number),
                ("status", status),
                ("amount", amount),
                ("customer_id", customer["id"]!)));

    private static long Id(IReadOnlyDictionary<string, object?> record) => Convert.ToInt64(record["id"]);

    private static async Task CorruptJoinedMetricAfterCompilationAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update _runtime.report_definitions
            set metadata = jsonb_set(metadata, '{Metrics,0,Field}', to_jsonb(cast(@field as text)))
            where id = 'sales_by_region'
            """;
        command.Parameters.AddWithValue("field", "customer.missing");
        await command.ExecuteNonQueryAsync();
    }

    private static ErpBundle JoinedReportBundle() => new()
    {
        Id = "joined_report_runtime",
        Name = "Joined report runtime",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "sales",
                Name = "Sales",
                Entities =
                [
                    Entity("order", "orders",
                        Field("order_number", "string"),
                        Field("status", "enum", options: ["open", "closed"]),
                        Field("amount", "money"),
                        Field("customer_id", "relation", references: "customer")),
                    Entity("customer", "customers",
                        Field("name", "string"),
                        Field("status", "enum", options: ["active", "inactive"]),
                        Field("region_id", "relation", references: "region")),
                    Entity("region", "regions",
                        Field("name", "string"),
                        Field("code", "string"))
                ]
            }
        ],
        Reports =
        [
            new ErpReport
            {
                Key = "sales_by_region",
                Label = "Sales by region",
                Source = "order",
                Joins =
                [
                    new ErpReportJoin { Alias = "customer", From = "source", Relation = "customer", Type = "inner" },
                    new ErpReportJoin { Alias = "region", From = "customer", Relation = "region", Type = "left" }
                ],
                GroupBy = [new ErpReportGroupBy { Field = "region.name", Label = "Region" }],
                Metrics =
                [
                    new ErpReportMetric { Key = "total_amount", Label = "Total amount", Op = "sum", Field = "source.amount" },
                    new ErpReportMetric
                    {
                        Key = "active_orders",
                        Label = "Active orders",
                        Op = "count",
                        Filter = new ErpReportFilter { Field = "customer.status", Op = "eq", Value = "active" }
                    }
                ],
                Filters =
                [
                    new ErpReportFilter { Field = "source.status", Op = "eq", Value = "open" },
                    new ErpReportFilter { Field = "region.code", Op = "eq", Value = "EU'WEST" }
                ]
            },
            new ErpReport
            {
                Key = "orders_by_status",
                Label = "Orders by status",
                Source = "order",
                GroupBy = [new ErpReportGroupBy { Field = "status", Label = "Status" }],
                Metrics = [new ErpReportMetric { Key = "total_amount", Label = "Total amount", Op = "sum", Field = "amount" }]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["analyst"] = Role("Analyst", "order", "customer", "region"),
            ["source_reader"] = Role("Source reader", "order")
        }
    };

    private static ErpEntity Entity(string id, string name, params ErpField[] fields) => new()
    {
        Id = id,
        Name = name,
        DisplayName = name.Replace('_', ' '),
        Fields = fields.ToList()
    };

    private static ErpField Field(
        string key,
        string type,
        string? references = null,
        IReadOnlyList<string>? options = null) => new()
    {
        Id = key,
        Name = key,
        DisplayName = key.Replace('_', ' '),
        Type = type,
        Required = true,
        References = references,
        Options = options?.ToList() ?? []
    };

    private static ErpRole Role(string label, params string[] entities) => new()
    {
        DisplayName = label,
        Grants = entities.Select(entity => new ErpGrant { Action = "read", Entity = entity }).ToList()
    };
}
