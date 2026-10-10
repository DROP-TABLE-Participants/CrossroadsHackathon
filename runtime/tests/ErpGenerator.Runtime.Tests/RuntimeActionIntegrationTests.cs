using System.Text.Json;
using FluentAssertions;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeActionIntegrationTests
{
    [PostgresFact]
    public async Task Declarative_invoice_draft_action_executes_transactionally_and_rolls_back_metadata()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var acmeDatabase = $"erp_tenant_action_acme_{suffix}";
        var betaDatabase = $"erp_tenant_action_beta_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, acmeDatabase);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, betaDatabase);

        try
        {
            var acme = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", acmeDatabase, TenantEnvironmentKind.Erp);
            var beta = RuntimeTestTenant.Create("beta", "beta.erpgenerator.example", betaDatabase, TenantEnvironmentKind.Erp);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var actions = new RuntimeActionStore(factory, new RuntimePermissionService(factory));
            var planner = new ErpBundleMigrationPlanner();
            var v3 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v3.yaml");
            var v4 = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp-v4.yaml");
            var owner = new RuntimeUserContext(
                1,
                "owner@acme.test",
                RuntimeSystemRoleIds.TenantOwner)
            {
                Roles = [RuntimeSystemRoleIds.TenantOwner]
            };
            var readonlyUser = new RuntimeUserContext(2, "readonly@acme.test", "readonly") { Roles = ["readonly"] };

            await runtimeDatabase.ApplyBundleDeploymentAsync(acme, v3, planner.PlanMigration(null, v3), "erp", "erp", activate: true, "v3 promoted");
            await runtimeDatabase.ApplyBundleDeploymentAsync(beta, v3, planner.PlanMigration(null, v3), "erp", "erp", activate: true, "beta v3 promoted");
            var v4Plan = planner.PlanMigration(await runtimeDatabase.ReadActiveCompiledBundleAsync(acme), v4);
            v4Plan.RiskLevel.Should().Be("safe");
            v4Plan.Operations.Should().Contain(operation => operation.Type == "add_action" && operation.Field == "create_invoice_draft");
            await runtimeDatabase.ApplyBundleDeploymentAsync(acme, v4, v4Plan, "erp", "erp", activate: true, "v4 promoted");

            var metadataV4 = await runtimeDatabase.ReadMetadataAsync(acme);
            metadataV4.BundleVersion.Should().Be("0.4.0");
            metadataV4.Actions.Should().Contain(action => action.GetProperty("Key").GetString() == "create_invoice_draft");

            var customer = await records.CreateRecordAsync(acme, owner, "customers", RuntimeJson.JsonObject(
                ("company_name", $"Acme {suffix}"),
                ("contact_email", $"acme-{suffix}@example.test"),
                ("status", "active")));
            var employee = await records.CreateRecordAsync(acme, owner, "employees", RuntimeJson.JsonObject(
                ("full_name", $"Consultant {suffix}"),
                ("email", $"consultant-{suffix}@example.test"),
                ("role", "Consultant"),
                ("hourly_cost", 50),
                ("weekly_capacity_hours", 40)));
            var project = await records.CreateRecordAsync(acme, owner, "projects", RuntimeJson.JsonObject(
                ("customer_id", customer["id"]!),
                ("project_manager_id", employee["id"]!),
                ("title", $"Action Project {suffix}"),
                ("budget", 10000),
                ("starts_on", "2026-07-03"),
                ("status", "active"),
                ("priority", "high")));
            var emptyProject = await records.CreateRecordAsync(acme, owner, "projects", RuntimeJson.JsonObject(
                ("customer_id", customer["id"]!),
                ("project_manager_id", employee["id"]!),
                ("title", $"Empty Project {suffix}"),
                ("budget", 10000),
                ("starts_on", "2026-07-03"),
                ("status", "active"),
                ("priority", "normal")));

            var noTimesheetAction = () => actions.ExecuteActionAsync(acme, owner, "projects", Convert.ToInt64(emptyProject["id"]), "create_invoice_draft");
            await noTimesheetAction.Should().ThrowAsync<RuntimeActionValidationException>()
                .WithMessage("No approved billable uninvoiced timesheets found.");
            (await records.ListRecordsAsync(acme, "invoices", new RuntimeRecordQuery(Search: $"DRAFT-{emptyProject["id"]}"))).Total.Should().Be(0);

            var unauthorizedAction = () => actions.ExecuteActionAsync(acme, readonlyUser, "projects", Convert.ToInt64(project["id"]), "create_invoice_draft");
            await unauthorizedAction.Should().ThrowAsync<RuntimeActionPermissionException>();

            var timesheet1 = await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", employee["id"]!),
                ("project_id", project["id"]!),
                ("logged_on", "2026-07-03"),
                ("hours", 6),
                ("billable", true),
                ("invoiced", false),
                ("billable_rate", 125),
                ("approval_status", "approved")));
            var timesheet2 = await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", employee["id"]!),
                ("project_id", project["id"]!),
                ("logged_on", "2026-07-04"),
                ("hours", 2.5),
                ("billable", true),
                ("billable_rate", 150),
                ("approval_status", "approved")));
            var excludedTimesheet = await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", employee["id"]!),
                ("project_id", project["id"]!),
                ("logged_on", "2026-07-05"),
                ("hours", 3),
                ("billable", false),
                ("billable_rate", 150),
                ("approval_status", "approved")));

            var result = await actions.ExecuteActionAsync(acme, owner, "projects", Convert.ToInt64(project["id"]), "create_invoice_draft");

            result.Success.Should().BeTrue();
            result.Created.Should().NotBeNull();
            result.Created!.Entity.Should().Be("invoices");
            result.Created.Url.Should().Be($"/entities/invoices/{result.Created.Id}");
            result.Counts["find_timesheets"].Should().Be(2);
            result.Counts["create_invoice_lines"].Should().Be(2);
            result.Counts["mark_timesheets_invoiced"].Should().Be(2);

            var invoice = await records.GetRecordAsync(acme, "invoices", result.Created.Id);
            invoice.Should().NotBeNull();
            invoice!["status"].Should().Be("draft");
            invoice["amount"].Should().Be(1125m);
            invoice["project_id"].Should().Be(Convert.ToInt64(project["id"]));
            invoice["customer_id"].Should().Be(Convert.ToInt64(customer["id"]));

            var invoiceLines = await records.ListRecordsAsync(acme, "invoice_lines", new RuntimeRecordQuery(PageSize: 100));
            invoiceLines.Records.Should().HaveCount(2);
            invoiceLines.Records.Should().Contain(line => Convert.ToInt64(line["timesheet_id"]) == Convert.ToInt64(timesheet1["id"]) && Convert.ToDecimal(line["amount"]) == 750m);
            invoiceLines.Records.Should().Contain(line => Convert.ToInt64(line["timesheet_id"]) == Convert.ToInt64(timesheet2["id"]) && Convert.ToDecimal(line["amount"]) == 375m);
            (await records.GetRecordAsync(acme, "timesheets", Convert.ToInt64(timesheet1["id"])))!["invoice_id"].Should().Be(result.Created.Id);
            (await records.GetRecordAsync(acme, "timesheets", Convert.ToInt64(timesheet1["id"])))!["invoiced"].Should().Be(true);
            (await records.GetRecordAsync(acme, "timesheets", Convert.ToInt64(timesheet2["id"])))!["invoice_id"].Should().Be(result.Created.Id);
            (await records.GetRecordAsync(acme, "timesheets", Convert.ToInt64(excludedTimesheet["id"])))!["invoice_id"].Should().BeNull();

            var rerun = () => actions.ExecuteActionAsync(acme, owner, "projects", Convert.ToInt64(project["id"]), "create_invoice_draft");
            await rerun.Should().ThrowAsync<RuntimeActionValidationException>()
                .WithMessage("No approved billable uninvoiced timesheets found.");
            (await records.ListRecordsAsync(acme, "invoice_lines", new RuntimeRecordQuery(PageSize: 100))).Records.Should().HaveCount(2);

            var failingProject = await records.CreateRecordAsync(acme, owner, "projects", RuntimeJson.JsonObject(
                ("customer_id", customer["id"]!),
                ("project_manager_id", employee["id"]!),
                ("title", $"Failing Project {suffix}"),
                ("budget", 10000),
                ("starts_on", "2026-07-03"),
                ("status", "active"),
                ("priority", "normal")));
            await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", employee["id"]!),
                ("project_id", failingProject["id"]!),
                ("logged_on", "2026-07-06"),
                ("hours", 4),
                ("billable", true),
                ("approval_status", "approved")));
            var partialFailure = () => actions.ExecuteActionAsync(acme, owner, "projects", Convert.ToInt64(failingProject["id"]), "create_invoice_draft");
            await partialFailure.Should().ThrowAsync<RuntimeActionValidationException>()
                .WithMessage("Unit price is required.");
            (await records.ListRecordsAsync(acme, "invoices", new RuntimeRecordQuery(Search: $"DRAFT-{failingProject["id"]}"))).Total.Should().Be(0);

            var archivedProject = await records.CreateRecordAsync(acme, owner, "projects", RuntimeJson.JsonObject(
                ("customer_id", customer["id"]!),
                ("project_manager_id", employee["id"]!),
                ("title", $"Archived Project {suffix}"),
                ("budget", 10000),
                ("starts_on", "2026-07-03"),
                ("status", "active"),
                ("priority", "normal")));
            await records.CreateRecordAsync(acme, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", employee["id"]!),
                ("project_id", archivedProject["id"]!),
                ("logged_on", "2026-07-07"),
                ("hours", 2),
                ("billable", true),
                ("billable_rate", 100),
                ("approval_status", "approved")));
            await records.ArchiveRecordAsync(acme, owner, "projects", Convert.ToInt64(archivedProject["id"]), "action archived source check");
            var archivedAction = () => actions.ExecuteActionAsync(acme, owner, "projects", Convert.ToInt64(archivedProject["id"]), "create_invoice_draft");
            await archivedAction.Should().ThrowAsync<RuntimeActionRecordNotFoundException>();

            var betaAction = () => actions.ExecuteActionAsync(beta, owner, "projects", Convert.ToInt64(project["id"]), "create_invoice_draft");
            await betaAction.Should().ThrowAsync<RuntimeActionNotFoundException>();

            var audit = await records.ListAuditEventsAsync(acme, 200);
            audit.Should().Contain(@event => @event.EventType == "action.started");
            audit.Should().Contain(@event => @event.EventType == "action.completed");
            audit.Should().Contain(@event => @event.EventType == "action.failed");
            audit.Should().Contain(@event => @event.EventType == "invoice.draft_created");
            audit.Should().Contain(@event => @event.EventType == "timesheet.marked_invoiced");
            audit.Should().Contain(@event => @event.EventType == "permission.denied");

            var rollback = await runtimeDatabase.RollbackActiveBundleAsync(acme);
            rollback.FromVersion.Should().Be("0.4.0");
            rollback.ToVersion.Should().Be("0.3.0");
            var afterRollback = await runtimeDatabase.ReadMetadataAsync(acme);
            afterRollback.BundleVersion.Should().Be("0.3.0");
            afterRollback.Actions.Should().NotContain(action => action.GetProperty("Key").GetString() == "create_invoice_draft");
            (await actions.GetActionAsync(acme, "projects", "create_invoice_draft")).Should().BeNull();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, acmeDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, betaDatabase);
        }
    }
}
