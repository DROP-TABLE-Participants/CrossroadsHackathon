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

public sealed class RuntimeReportRecordScopeIntegrationTests
{
    [PostgresFact]
    public async Task Reports_filter_source_and_join_rows_before_aggregates_and_preserve_left_join_semantics()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_report_scope_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "report-scope",
                "report-scope.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var reports = new RuntimeReportStore(factory);
            var compiled = new ErpBundleCompiler().Compile(ReportScopeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var system = User(-900, RuntimeSystemRoleIds.TenantOwner);
            var visibleCustomer = await CreateCustomerAsync(
                tenant,
                records,
                system,
                "Visible customer",
                ownerPrincipal: 101,
                creditLimit: 100m);
            var hiddenCustomer = await CreateCustomerAsync(
                tenant,
                records,
                system,
                "Hidden customer",
                ownerPrincipal: 202,
                creditLimit: 200m);
            await CreateInvoiceAsync(tenant, records, system, "90-VISIBLE", 10m, 101, visibleCustomer);
            await CreateInvoiceAsync(tenant, records, system, "00-HIDDEN", 20m, 202, hiddenCustomer);
            await CreateInvoiceAsync(tenant, records, system, "91-VISIBLE", 30m, 101, hiddenCustomer);
            await CreateInvoiceAsync(tenant, records, system, "01-HIDDEN", 40m, 202, hiddenCustomer);
            await CreateInvoiceAsync(tenant, records, system, "02-HIDDEN", 50m, 202, hiddenCustomer);

            var sourceEntity = (await records.GetEntityAsync(tenant, "invoice"))!;
            var sourceReport = (await reports.GetReportAsync(tenant, "source_totals"))!;
            var sourceRowsReport = (await reports.GetReportAsync(tenant, "source_rows"))!;
            var innerReport = (await reports.GetReportAsync(tenant, "inner_customer_totals"))!;
            var leftReport = (await reports.GetReportAsync(tenant, "left_customer_totals"))!;

            var sourceScoped = User(101, "source_scoped");
            var sourceResult = await reports.ExecuteReportAsync(
                tenant,
                sourceScoped,
                sourceReport,
                sourceEntity,
                new RuntimeReportQuery());
            Metric(sourceResult, "invoice_count").Should().Be(2L);
            Metric(sourceResult, "total_amount").Should().Be(40m);

            var sourceRows = await reports.ExecuteReportAsync(
                tenant,
                sourceScoped,
                sourceRowsReport,
                sourceEntity,
                new RuntimeReportQuery(Limit: 2));
            sourceRows.Rows.Select(row => row["number"]).Should().Equal("90-VISIBLE", "91-VISIBLE");
            sourceRows.IsTruncated.Should().BeFalse(
                "three denied rows exceed the query limit but must be filtered before grouping and LIMIT");

            var noSourceRead = async () => await reports.ExecuteReportAsync(
                tenant,
                User(101, "target_only"),
                sourceReport,
                sourceEntity,
                new RuntimeReportQuery());
            await noSourceRead.Should().ThrowAsync<RuntimeRecordPermissionException>();

            var restrictedMetric = async () => await reports.ExecuteReportAsync(
                tenant,
                User(401, "restricted_metric"),
                sourceReport,
                sourceEntity,
                new RuntimeReportQuery());
            await restrictedMetric.Should().ThrowAsync<RuntimeRecordPermissionException>(
                "metric fields must be authorized inside the report store, not only by route preflight");

            var dateAuthorizedWithoutWindow = await reports.ExecuteReportAsync(
                tenant,
                User(402, "restricted_date"),
                sourceReport,
                sourceEntity,
                new RuntimeReportQuery());
            Metric(dateAuthorizedWithoutWindow, "total_amount").Should().Be(150m);
            var restrictedDateWindow = async () => await reports.ExecuteReportAsync(
                tenant,
                User(402, "restricted_date"),
                sourceReport,
                sourceEntity,
                new RuntimeReportQuery(DateFrom: new DateOnly(2026, 1, 1)));
            await restrictedDateWindow.Should().ThrowAsync<RuntimeRecordPermissionException>(
                "the store-selected date field is an authoritative report dependency");

            var joinScoped = User(101, "join_scoped");
            var innerResult = await reports.ExecuteReportAsync(
                tenant,
                joinScoped,
                innerReport,
                sourceEntity,
                new RuntimeReportQuery());
            Metric(innerResult, "invoice_count").Should().Be(1L,
                "an inner join must remove source rows whose joined target is outside the actor's record scope");
            Metric(innerResult, "total_amount").Should().Be(10m);
            Metric(innerResult, "visible_credit").Should().Be(100m);

            var restrictedJoinRelation = async () => await reports.ExecuteReportAsync(
                tenant,
                User(403, "restricted_join_relation"),
                innerReport,
                sourceEntity,
                new RuntimeReportQuery());
            await restrictedJoinRelation.Should().ThrowAsync<RuntimeRecordPermissionException>(
                "a joined relation key must be readable even when all selected metric fields are readable");

            var leftResult = await reports.ExecuteReportAsync(
                tenant,
                joinScoped,
                leftReport,
                sourceEntity,
                new RuntimeReportQuery());
            Metric(leftResult, "invoice_count").Should().Be(5L,
                "a left join keeps every authorized source row even when its target is hidden");
            Metric(leftResult, "total_amount").Should().Be(150m);
            Metric(leftResult, "visible_customer_count").Should().Be(1L);
            Metric(leftResult, "visible_credit").Should().Be(100m);

            var missingJoinCapability = async () => await reports.ExecuteReportAsync(
                tenant,
                User(101, "source_only"),
                leftReport,
                sourceEntity,
                new RuntimeReportQuery());
            await missingJoinCapability.Should().ThrowAsync<RuntimeRecordPermissionException>(
                "a left join must not turn missing target read capability into an aggregate bypass");

            var legacy = User(303, "legacy_analyst");
            var legacySource = await reports.ExecuteReportAsync(
                tenant,
                legacy,
                sourceReport,
                sourceEntity,
                new RuntimeReportQuery());
            Metric(legacySource, "invoice_count").Should().Be(5L);
            Metric(legacySource, "total_amount").Should().Be(150m);

            var legacyInner = await reports.ExecuteReportAsync(
                tenant,
                legacy,
                innerReport,
                sourceEntity,
                new RuntimeReportQuery());
            Metric(legacyInner, "invoice_count").Should().Be(5L);
            Metric(legacyInner, "total_amount").Should().Be(150m);

            var legacyLeft = await reports.ExecuteReportAsync(
                tenant,
                legacy,
                leftReport,
                sourceEntity,
                new RuntimeReportQuery());
            Metric(legacyLeft, "invoice_count").Should().Be(5L);
            Metric(legacyLeft, "visible_customer_count").Should().Be(5L);
            Metric(legacyLeft, "visible_credit").Should().Be(900m,
                "entity-scoped legacy reads retain the pre-record-scope aggregate behavior");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Dashboard_metric_equality_consumes_the_actor_scoped_report_output()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_dashboard_scope_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "dashboard-scope",
                "dashboard-scope.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var reports = new RuntimeReportStore(factory);
            var compiled = new ErpBundleCompiler().Compile(ReportScopeBundle());
            await runtimeDatabase.ApplyCompiledBundleAsync(tenant, compiled);

            var seed = new BundleSeedDocument
            {
                Seed = new BundleSeed { Key = "hidden_invoice", Label = "Hidden invoice" },
                Records =
                [
                    new BundleSeedRecord
                    {
                        Ref = "hidden_invoice",
                        Entity = "invoice",
                        Values = new Dictionary<string, object?>
                        {
                            ["number"] = $"HIDDEN-{suffix}",
                            ["amount"] = 100m,
                            ["owner_principal"] = 999
                        }
                    }
                ]
            };
            var actorToken = BundleAcceptanceActorPrincipalToken.For("dashboard_actor");
            var test = new BundleAcceptanceTestDocument
            {
                Schema = BundleAcceptanceTestSchemas.V3,
                Test = new BundleAcceptanceTest
                {
                    Key = "actor_scoped_dashboard",
                    Label = "Actor scoped dashboard"
                },
                Setup = new BundleAcceptanceSetup
                {
                    Seed = seed.Seed.Key,
                    SeedHash = BundleSeedContractHasher.ComputeHash(seed)
                },
                Actors =
                [
                    new BundleAcceptanceActor
                    {
                        Id = "dashboard_actor",
                        Roles = ["dashboard_actor"]
                    }
                ],
                Steps =
                [
                    new BundleAcceptanceStep
                    {
                        Id = "create_visible_invoice",
                        Type = "create_record",
                        Entity = "invoice",
                        AsRole = "dashboard_actor",
                        AsActor = "dashboard_actor",
                        Values = new Dictionary<string, object?>
                        {
                            ["number"] = $"VISIBLE-{suffix}",
                            ["amount"] = 7m,
                            ["owner_principal"] = actorToken
                        }
                    },
                    new BundleAcceptanceStep
                    {
                        Id = "call_source_totals",
                        Type = "call_report",
                        Report = "source_totals",
                        AsRole = "dashboard_actor",
                        AsActor = "dashboard_actor"
                    }
                ],
                Assertions =
                [
                    new BundleAcceptanceAssertion
                    {
                        Id = "dashboard_uses_scoped_total",
                        AfterStep = "call_source_totals",
                        Type = "dashboard_metric_equals",
                        Widget = "invoice_amount",
                        Report = "source_totals",
                        ReportRef = "call_source_totals",
                        Metric = "total_amount",
                        Value = 7m
                    }
                ]
            };
            BundleAcceptanceTestContractHasher.Stamp([test]);
            var runner = new RuntimeBundleTestRunner(
                factory,
                new RuntimeSeedRunner(records),
                records,
                new RuntimeWorkflowStore(factory),
                new RuntimeActionStore(factory, new RuntimePermissionService(factory)),
                reports);

            var result = await runner.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed,
                [test]);

            result.Status.Should().Be("passed");
            result.Results.Should().ContainSingle().Which.AssertionObservations.Should().ContainSingle()
                .Which.Should().Match<RuntimeBundleTestAssertionObservation>(observation =>
                    observation.Status == RuntimeBundleTestAssertionObservationStatuses.Passed &&
                    observation.ActualValueKind == BundleAcceptanceEvidenceValueKinds.Decimal &&
                    observation.ActualValue == null &&
                    observation.ActualValueDigest == BundleAcceptanceEvidenceDigestContract.Create(
                        BundleAcceptanceEvidenceValueKinds.Decimal,
                        7m).Digest);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Http_report_surfaces_propagate_actor_scope_and_map_late_field_denials_safely()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_report_scope_http_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "report-scope-http",
                "report-scope-http.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var users = new RuntimeUserStore(factory);
            await runtimeDatabase.ApplyCompiledBundleAsync(
                tenant,
                new ErpBundleCompiler().Compile(ReportScopeBundle()));
            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                runtimeDatabase,
                tenant,
                $"owner-{suffix}@report.test");
            var scopedUser = await InviteAndActivateAsync(
                users,
                runtimeDatabase,
                tenant,
                owner,
                $"scoped-{suffix}@report.test",
                "source_scoped");
            var restrictedDateUser = await InviteAndActivateAsync(
                users,
                runtimeDatabase,
                tenant,
                owner,
                $"date-{suffix}@report.test",
                "restricted_date");

            var customer = await CreateCustomerAsync(
                tenant,
                records,
                owner,
                "HTTP customer",
                scopedUser.UserId,
                100m);
            await CreateInvoiceAsync(tenant, records, owner, "HTTP-VISIBLE-1", 10m, scopedUser.UserId, customer);
            await CreateInvoiceAsync(tenant, records, owner, "HTTP-VISIBLE-2", 30m, scopedUser.UserId, customer);
            await CreateInvoiceAsync(tenant, records, owner, "HTTP-HIDDEN", 110m, owner.UserId, customer);

            var sessionSecret = $"report-scope-http-{suffix}";
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
                        services.AddSingleton<IControlPlaneClient>(new StaticControlPlaneClient(tenant));
                    });
                });
            using var client = application.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var scopedCookie = RuntimeHttpTestHelper.CreateSessionCookie(
                sessionSecret,
                tenant.TenantStableId,
                tenant.Host,
                scopedUser.UserId,
                scopedUser.Email,
                scopedUser.Role);

            using (var jsonRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                       HttpMethod.Get,
                       "/api/runtime/reports/source_totals",
                       tenant.Host,
                       scopedCookie))
            {
                var response = await client.SendAsync(jsonRequest);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var row = json.RootElement.GetProperty("rows")[0];
                row.GetProperty("invoice_count").GetInt64().Should().Be(2);
                row.GetProperty("total_amount").GetDecimal().Should().Be(40m);
            }

            using (var csvRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                       HttpMethod.Get,
                       "/api/runtime/reports/source_totals.csv",
                       tenant.Host,
                       scopedCookie))
            {
                var response = await client.SendAsync(csvRequest);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var csv = await response.Content.ReadAsStringAsync();
                csv.Should().Contain("2,40");
                csv.Should().NotContain("3,150");
            }

            using (var pageRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                       HttpMethod.Get,
                       "/reports/source_totals",
                       tenant.Host,
                       scopedCookie))
            {
                var response = await client.SendAsync(pageRequest);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var html = await response.Content.ReadAsStringAsync();
                html.Should().Contain("Source totals");
                html.Should().Contain("<strong>40</strong>");
                html.Should().NotContain("<strong>150</strong>");
            }

            using (var dashboardRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                       HttpMethod.Get,
                       "/",
                       tenant.Host,
                       scopedCookie))
            {
                var response = await client.SendAsync(dashboardRequest);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var html = await response.Content.ReadAsStringAsync();
                html.Should().Contain("Invoice amount");
                html.Should().Contain("<strong>40</strong>");
                html.Should().NotContain("<strong>150</strong>");
            }

            var restrictedDateCookie = RuntimeHttpTestHelper.CreateSessionCookie(
                sessionSecret,
                tenant.TenantStableId,
                tenant.Host,
                restrictedDateUser.UserId,
                restrictedDateUser.Email,
                restrictedDateUser.Role);
            foreach (var path in new[]
                     {
                         "/api/runtime/reports/source_totals?dateFrom=2026-07-01",
                         "/api/runtime/reports/source_totals.csv?dateFrom=2026-07-01",
                         "/reports/source_totals?dateFrom=2026-07-01"
                     })
            {
                using var request = RuntimeHttpTestHelper.AuthenticatedRequest(
                    HttpMethod.Get,
                    path,
                    tenant.Host,
                    restrictedDateCookie);
                (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }

            using (var dashboardRequest = RuntimeHttpTestHelper.AuthenticatedRequest(
                       HttpMethod.Get,
                       "/",
                       tenant.Host,
                       restrictedDateCookie))
            {
                var response = await client.SendAsync(dashboardRequest);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var html = await response.Content.ReadAsStringAsync();
                html.Should().Contain("Invoice amount");
                html.Should().NotContain("Invoice amount this month",
                    "a late date-field denial must omit only the affected dashboard widget");
            }
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static object? Metric(RuntimeReportResult result, string key) =>
        result.Rows.Should().ContainSingle().Which[key];

    private static RuntimeUserContext User(long id, string role) => new(id, $"{role}@report.test", role)
    {
        Roles = [role]
    };

    private static async Task<RuntimeUserContext> InviteAndActivateAsync(
        RuntimeUserStore users,
        RuntimeDatabase runtimeDatabase,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext owner,
        string email,
        string role)
    {
        var invite = await users.InviteUserAsync(
            tenant,
            owner,
            email,
            role,
            [role],
            employeeRecordId: null,
            lifetime: TimeSpan.FromDays(1));
        await users.ActivateInvitedUserAsync(
            tenant,
            invite.ActivationToken!,
            email,
            role,
            "MemberPass123!");
        var login = await runtimeDatabase.ValidateLoginAsync(tenant, email, "MemberPass123!");
        return new RuntimeUserContext(login!.Id, login.Email, login.Role)
        {
            Roles = login.Roles
        };
    }

    private static Task<Dictionary<string, object?>> CreateCustomerAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeRecordStore records,
        RuntimeUserContext user,
        string name,
        long ownerPrincipal,
        decimal creditLimit) =>
        records.CreateRecordAsync(
            tenant,
            user,
            "customer",
            RuntimeJson.JsonObject(
                ("name", name),
                ("owner_principal", ownerPrincipal),
                ("credit_limit", creditLimit)));

    private static Task<Dictionary<string, object?>> CreateInvoiceAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeRecordStore records,
        RuntimeUserContext user,
        string number,
        decimal amount,
        long ownerPrincipal,
        IReadOnlyDictionary<string, object?> customer) =>
        records.CreateRecordAsync(
            tenant,
            user,
            "invoice",
            RuntimeJson.JsonObject(
                ("number", number),
                ("amount", amount),
                ("owner_principal", ownerPrincipal),
                ("issued_on", "2026-07-11"),
                ("customer_id", customer["id"]!)));

    private static ErpBundle ReportScopeBundle() => new()
    {
        Id = "report_record_scope",
        Name = "Report record scope",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "billing",
                Name = "Billing",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "invoice",
                        Name = "invoices",
                        DisplayName = "Invoices",
                        Fields =
                        [
                            Field("number", "number", "string"),
                            Field("amount", "amount", "money"),
                            Field("owner_principal", "owner_principal", "integer"),
                            Field("issued_on", "issued_on", "date", required: false),
                            Field("customer", "customer_id", "relation", required: false, references: "customer")
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "customer",
                        Name = "customers",
                        DisplayName = "Customers",
                        Fields =
                        [
                            Field("name", "name", "string"),
                            Field("credit_limit", "credit_limit", "money"),
                            Field("owner_principal", "owner_principal", "integer")
                        ]
                    }
                ]
            }
        ],
        Reports =
        [
            new ErpReport
            {
                Key = "source_totals",
                Label = "Source totals",
                Source = "invoice",
                Metrics =
                [
                    Metric("invoice_count", "Invoice count", "count"),
                    Metric("total_amount", "Total amount", "sum", "amount")
                ]
            },
            new ErpReport
            {
                Key = "source_rows",
                Label = "Source rows",
                Source = "invoice",
                GroupBy = [new ErpReportGroupBy { Field = "number", Label = "Number" }],
                Metrics = [Metric("invoice_count", "Invoice count", "count")]
            },
            new ErpReport
            {
                Key = "inner_customer_totals",
                Label = "Inner customer totals",
                Source = "invoice",
                Joins = [new ErpReportJoin { Alias = "customer", From = "source", Relation = "customer", Type = "inner" }],
                Metrics =
                [
                    Metric("invoice_count", "Invoice count", "count"),
                    Metric("total_amount", "Total amount", "sum", "source.amount"),
                    Metric("visible_credit", "Visible credit", "sum", "customer.credit_limit")
                ]
            },
            new ErpReport
            {
                Key = "left_customer_totals",
                Label = "Left customer totals",
                Source = "invoice",
                Joins = [new ErpReportJoin { Alias = "customer", From = "source", Relation = "customer", Type = "left" }],
                Metrics =
                [
                    Metric("invoice_count", "Invoice count", "count"),
                    Metric("total_amount", "Total amount", "sum", "source.amount"),
                    Metric("visible_customer_count", "Visible customer count", "count", "customer.name"),
                    Metric("visible_credit", "Visible credit", "sum", "customer.credit_limit")
                ]
            }
        ],
        Dashboard = new ErpDashboard
        {
            Widgets =
            [
                new ErpDashboardWidget
                {
                    Key = "invoice_amount",
                    Label = "Invoice amount",
                    Type = "metric",
                    Report = "source_totals",
                    Metric = "total_amount"
                },
                new ErpDashboardWidget
                {
                    Key = "invoice_amount_this_month",
                    Label = "Invoice amount this month",
                    Type = "metric",
                    Report = "source_totals",
                    Metric = "total_amount"
                }
            ]
        },
        Roles = new Dictionary<string, ErpRole>
        {
            ["source_scoped"] = Role(RecordRead("invoice", "owner_principal")),
            ["join_scoped"] = Role(EntityRead("invoice"), RecordRead("customer", "owner_principal")),
            ["legacy_analyst"] = Role(EntityRead("invoice"), EntityRead("customer")),
            ["source_only"] = Role(EntityRead("invoice")),
            ["target_only"] = Role(EntityRead("customer")),
            ["restricted_metric"] = Role(
                EntityRead("invoice"),
                FieldRead("invoice", "number")),
            ["restricted_date"] = Role(
                EntityRead("invoice"),
                FieldRead("invoice", "amount")),
            ["restricted_join_relation"] = Role(
                EntityRead("invoice"),
                EntityRead("customer"),
                FieldRead("invoice", "amount"),
                FieldRead("customer", "credit_limit")),
            ["dashboard_actor"] = Role(
                new ErpGrant { Action = "create", Entity = "invoice" },
                RecordRead("invoice", "owner_principal"))
        }
    };

    private static ErpField Field(
        string id,
        string name,
        string type,
        bool required = true,
        string? references = null) => new()
    {
        Id = id,
        Name = name,
        DisplayName = id.Replace('_', ' '),
        Type = type,
        Required = required,
        References = references
    };

    private static ErpReportMetric Metric(string key, string label, string op, string? field = null) => new()
    {
        Key = key,
        Label = label,
        Op = op,
        Field = field ?? string.Empty
    };

    private static ErpRole Role(params ErpGrant[] grants) => new()
    {
        DisplayName = "Report role",
        Grants = grants.ToList()
    };

    private static ErpGrant EntityRead(string entity) => new()
    {
        Action = "read",
        Entity = entity
    };

    private static ErpGrant RecordRead(string entity, string field) => new()
    {
        Action = "read",
        Entity = entity,
        Scope = PermissionGrantScopes.Record,
        RecordScope = new ErpRecordScope
        {
            Field = field,
            Source = RecordPermissionScopeSources.ActorPrincipal
        }
    };

    private static ErpGrant FieldRead(string entity, string field) => new()
    {
        Action = GranularPermissionActions.Read,
        Entity = entity,
        Scope = PermissionGrantScopes.Field,
        Target = field
    };
}
