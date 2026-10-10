using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeReportIntegrationTests
{
    [PostgresFact]
    public async Task Reports_computed_fields_dashboard_and_permissions_are_tenant_safe()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var acmeDatabase = $"erp_tenant_report_acme_{suffix}";
        var betaDatabase = $"erp_tenant_report_beta_{suffix}";
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
            var compiled = await RuntimeBundleTestHelper.CompileSampleBundleAsync();
            await runtimeDatabase.ApplyCompiledBundleAsync(acme, compiled);
            await runtimeDatabase.ApplyCompiledBundleAsync(beta, compiled);

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(runtimeDatabase, acme, "owner@acme.test");
            var member = await RuntimeUserTestHelper.InviteAndActivateMemberAsync(users, runtimeDatabase, acme, owner, "member@acme.test");

            var metadata = await runtimeDatabase.ReadMetadataAsync(acme);
            metadata.Reports.Should().Contain(report => report.GetProperty("Key").GetString() == "project_hours");
            metadata.Dashboard.Should().NotBeNull();

            await SeedReportDataAsync(acme, records, owner, suffix, hours: 5, archivedHours: 99, invoiceAmount: 1000);
            await SeedReportDataAsync(beta, records, new RuntimeUserContext(1, "owner@beta.test", "owner"), suffix, hours: 8, archivedHours: 25, invoiceAmount: 2000);

            var projectHours = await reports.GetReportAsync(acme, "project_hours");
            projectHours.Should().NotBeNull();
            var timesheets = (await records.GetEntityAsync(acme, "timesheets"))!;
            var projectHoursResult = await reports.ExecuteReportAsync(acme, owner, projectHours!, timesheets, new RuntimeReportQuery(), CancellationToken.None);
            projectHoursResult.Rows.Should().ContainSingle();
            var row = projectHoursResult.Rows.Single();
            row["total_hours"].Should().Be(5m);
            row["billable_hours"].Should().Be(5m);
            row["billable_amount"].Should().Be(500m);
            row.Should().NotContainKey("project__label",
                "reports must not hydrate undeclared target-entity labels across permission boundaries");
            projectHoursResult.Columns.Single(column => column.Key == "project")
                .Should().Match<RuntimeReportColumn>(column =>
                    column.Kind == RuntimeReportColumnKinds.Identity &&
                    !string.IsNullOrWhiteSpace(column.IdentityEntityId));

            var acmeTimesheets = await records.ListRecordsAsync(acme, "timesheets", new RuntimeRecordQuery());
            acmeTimesheets.Records.Should().ContainSingle(record => Convert.ToDecimal(record["billable_amount"]) == 500m);
            var writeComputed = async () => await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", acmeTimesheets.Records.Single()["employee_id"]!),
                ("project_id", acmeTimesheets.Records.Single()["project_id"]!),
                ("logged_on", "2026-07-03"),
                ("hours", 1),
                ("billable_rate", 100),
                ("billable_amount", 100)));
            await writeComputed.Should().ThrowAsync<RuntimeRecordValidationException>()
                .Where(ex => ex.Errors.Any(error => error.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase)));

            var betaProjectHours = await reports.ExecuteReportAsync(
                beta,
                new RuntimeUserContext(-1, "system@beta.test", RuntimeSystemRoleIds.TenantOwner),
                projectHours!,
                (await records.GetEntityAsync(beta, "timesheets"))!,
                new RuntimeReportQuery(),
                CancellationToken.None);
            betaProjectHours.Rows.Single()["total_hours"].Should().Be(8m);
            betaProjectHours.Rows.Single()["total_hours"].Should().NotBe(row["total_hours"]);

            var sessionSecret = $"report-api-test-{suffix}";
            var controlPlane = new CapturingUsageControlPlaneClient(acme, beta);
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
                        services.AddSingleton<IControlPlaneClient>(controlPlane);
                    });
                });
            using var client = application.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var ownerCookie = RuntimeHttpTestHelper.CreateSessionCookie(sessionSecret, acme.TenantStableId, acme.Host, owner.UserId, owner.Email, owner.Role);
            var memberCookie = RuntimeHttpTestHelper.CreateSessionCookie(sessionSecret, acme.TenantStableId, acme.Host, member.UserId, member.Email, member.Role);

            using var reportsIndexRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/api/runtime/reports", acme.Host, ownerCookie);
            var reportsIndexResponse = await client.SendAsync(reportsIndexRequest);
            reportsIndexResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var reportsJson = JsonDocument.Parse(await reportsIndexResponse.Content.ReadAsStringAsync());
            reportsJson.RootElement.EnumerateArray().Should().Contain(report => report.GetProperty("key").GetString() == "project_hours");

            using var reportDetailRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/api/runtime/reports/unpaid_invoices", acme.Host, ownerCookie);
            var reportDetailResponse = await client.SendAsync(reportDetailRequest);
            reportDetailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var detailJson = JsonDocument.Parse(await reportDetailResponse.Content.ReadAsStringAsync());
            detailJson.RootElement.GetProperty("rows")[0].GetProperty("unpaid_amount").GetDecimal().Should().Be(1000m);

            using var reportCsvRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/api/runtime/reports/unpaid_invoices.csv", acme.Host, ownerCookie);
            var reportCsvResponse = await client.SendAsync(reportCsvRequest);
            reportCsvResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            reportCsvResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
            var reportCsv = await reportCsvResponse.Content.ReadAsStringAsync();
            reportCsv.Should().Contain("Unpaid amount");
            reportCsv.Should().Contain("1000");
            controlPlane.UsageEvents.Should().Contain(usage =>
                usage.EventKind == "report_viewed" &&
                usage.ReportKey == "unpaid_invoices" &&
                usage.EntityKey == "invoices");
            controlPlane.UsageEvents.Should().Contain(usage =>
                usage.EventKind == "report_exported" &&
                usage.ReportKey == "unpaid_invoices" &&
                usage.EntityKey == "invoices");

            using var deniedRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/api/runtime/reports/unpaid_invoices", acme.Host, memberCookie);
            var deniedResponse = await client.SendAsync(deniedRequest);
            deniedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var deniedCsvRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/api/runtime/reports/unpaid_invoices.csv", acme.Host, memberCookie);
            var deniedCsvResponse = await client.SendAsync(deniedCsvRequest);
            deniedCsvResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var auditEvents = await records.ListAuditEventsAsync(acme, 20);
            auditEvents.Should().Contain(@event => @event.EventType == "report.viewed" && @event.Metadata.GetProperty("report").GetString() == "unpaid_invoices");
            auditEvents.Should().Contain(@event => @event.EventType == "report.permission_denied" && @event.Metadata.GetProperty("report").GetString() == "unpaid_invoices");

            using var dashboardRequest = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/", acme.Host, ownerCookie);
            var dashboardResponse = await client.SendAsync(dashboardRequest);
            dashboardResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var dashboardHtml = await dashboardResponse.Content.ReadAsStringAsync();
            dashboardHtml.Should().Contain("Total customers");
            dashboardHtml.Should().Contain("Billable hours this month");
            dashboardHtml.Should().Contain("Unpaid invoice amount");
            dashboardHtml.Should().Contain("href=\"/entities/customers\"");
            dashboardHtml.Should().Contain("href=\"/reports/project_hours\"");
            dashboardHtml.Should().Contain("href=\"/reports/unpaid_invoices\"");

            using var betaHostWithAcmeCookie = RuntimeHttpTestHelper.AuthenticatedRequest(HttpMethod.Get, "/api/runtime/reports/project_hours", beta.Host, ownerCookie);
            var betaHostResponse = await client.SendAsync(betaHostWithAcmeCookie);
            betaHostResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, acmeDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, betaDatabase);
        }
    }

    [PostgresFact]
    public async Task Computed_divide_by_zero_returns_null_on_detail_and_list()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databaseName = $"erp_tenant_computed_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var tenant = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", databaseName);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, new ErpBundleCompiler().Compile(DivisionBundle()));
            var owner = new RuntimeUserContext(1, "analyst@acme.test", "measurement_analyst")
            {
                Roles = ["measurement_analyst"]
            };

            var record = await records.CreateRecordAsync(tenant, owner, "measurements", RuntimeJson.JsonObject(
                ("numerator", 10),
                ("denominator", 0)));

            record["ratio"].Should().BeNull();
            var detail = await records.GetRecordAsync(tenant, "measurements", Convert.ToInt64(record["id"]));
            detail!["ratio"].Should().BeNull();
            var list = await records.ListRecordsAsync(tenant, "measurements", new RuntimeRecordQuery());
            list.Records.Single()["ratio"].Should().BeNull();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static async Task SeedReportDataAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeRecordStore records,
        RuntimeUserContext user,
        string suffix,
        decimal hours,
        decimal archivedHours,
        decimal invoiceAmount)
    {
        var customer = await records.CreateRecordAsync(tenant, user, "customers", RuntimeJson.JsonObject(
            ("company_name", $"{tenant.TenantSlug} Customer {suffix}"),
            ("contact_email", $"{tenant.TenantSlug}-{suffix}@example.test"),
            ("status", "active")));
        var employee = await records.CreateRecordAsync(tenant, user, "employees", RuntimeJson.JsonObject(
            ("full_name", $"{tenant.TenantSlug} Employee {suffix}"),
            ("email", $"{tenant.TenantSlug}-employee-{suffix}@example.test"),
            ("role", "Consultant"),
            ("title", "Consultant"),
            ("hourly_cost", 40),
            ("weekly_capacity_hours", 40)));
        var project = await records.CreateRecordAsync(tenant, user, "projects", RuntimeJson.JsonObject(
            ("customer_id", customer["id"]!),
            ("title", $"{tenant.TenantSlug} Project {suffix}"),
            ("budget", 5000),
            ("starts_on", "2026-07-01"),
            ("project_manager_id", employee["id"]!),
            ("status", "active")));
        await records.CreateRecordAsync(tenant, user, "timesheets", RuntimeJson.JsonObject(
            ("employee_id", employee["id"]!),
            ("project_id", project["id"]!),
            ("logged_on", "2026-07-02"),
            ("hours", hours),
            ("billable", true),
            ("billable_rate", 100),
            ("approval_status", "approved")));
        var archived = await records.CreateRecordAsync(tenant, user, "timesheets", RuntimeJson.JsonObject(
            ("employee_id", employee["id"]!),
            ("project_id", project["id"]!),
            ("logged_on", "2026-07-02"),
            ("hours", archivedHours),
            ("billable", true),
            ("billable_rate", 100),
            ("approval_status", "approved")));
        await records.ArchiveRecordAsync(tenant, user, "timesheets", Convert.ToInt64(archived["id"]), "report exclusion");
        await records.CreateRecordAsync(tenant, user, "invoices", RuntimeJson.JsonObject(
            ("customer_id", customer["id"]!),
            ("project_id", project["id"]!),
            ("invoice_number", $"INV-{tenant.TenantSlug}-{suffix}"),
            ("issued_on", "2026-07-02"),
            ("amount", invoiceAmount),
            ("status", "sent"),
            ("due_date", "2026-07-31")));
    }

    private static ErpBundle DivisionBundle() => new()
    {
        Id = "division_bundle",
        Name = "Division bundle",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "ops",
                Name = "Ops",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "measurement",
                        Name = "measurements",
                        DisplayName = "Measurements",
                        Fields =
                        [
                            new ErpField { Id = "numerator", Name = "numerator", DisplayName = "Numerator", Type = "number", Required = true },
                            new ErpField { Id = "denominator", Name = "denominator", DisplayName = "Denominator", Type = "number", Required = true },
                            new ErpField
                            {
                                Id = "ratio",
                                Name = "ratio",
                                DisplayName = "Ratio",
                                Type = "number",
                                Computed = new ErpComputedField
                                {
                                    Op = "divide",
                                    Left = new ErpComputedOperand { Field = "numerator" },
                                    Right = new ErpComputedOperand { Field = "denominator" }
                                }
                            }
                        ]
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["measurement_analyst"] = new()
            {
                DisplayName = "Measurement analyst",
                Grants =
                [
                    new ErpGrant { Entity = "measurement", Action = "read" },
                    new ErpGrant { Entity = "measurement", Action = "create" }
                ]
            }
        }
    };

}
