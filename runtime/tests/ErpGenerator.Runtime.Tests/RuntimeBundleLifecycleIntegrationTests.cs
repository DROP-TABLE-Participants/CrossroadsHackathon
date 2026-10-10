using System.Text.Json;
using FluentAssertions;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeBundleLifecycleIntegrationTests
{
    [PostgresFact]
    public async Task First_blocked_deployment_is_recorded_on_a_fresh_tenant_database()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var databaseName = $"erp_tenant_first_blocked_{Guid.NewGuid():N}"[..45];
        var tenant = RuntimeTestTenant.Create(
            "first-blocked",
            "first-blocked.erpgenerator.example",
            databaseName,
            TenantEnvironmentKind.Erp);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, databaseName);

        try
        {
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var bundle = await RuntimeBundleTestHelper.CompileBundleFileAsync("sample-erp.yaml");
            var blockedPlan = new BundleMigrationPlan(
                null,
                bundle.Version,
                "blocked",
                [],
                ["Synthetic first-deployment blocker."]);

            var result = await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                bundle,
                blockedPlan,
                "erp",
                "failed",
                activate: false,
                message: "first deployment blocked");

            result.Status.Should().Be("failed");
            (await runtimeDatabase.ReadDeploymentHistoryAsync(tenant)).Should().ContainSingle(item =>
                item.Status == "failed" && item.RiskLevel == "blocked");
            (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant)).Should().BeNull();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [PostgresFact]
    public async Task Bundle_lifecycle_promotes_it_services_v3_and_rolls_back_metadata_to_v2_without_data_loss()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var acmeStagingDatabase = $"erp_tenant_lifecycle_acme_stg_{suffix}";
        var acmeProductionDatabase = $"erp_tenant_lifecycle_acme_prod_{suffix}";
        var betaProductionDatabase = $"erp_tenant_lifecycle_beta_prod_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, acmeStagingDatabase);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, acmeProductionDatabase);
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, betaProductionDatabase);

        try
        {
            var acmeStaging = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", acmeStagingDatabase, TenantEnvironmentKind.Erp);
            var acmeProduction = RuntimeTestTenant.Create("acme", "acme.erpgenerator.example", acmeProductionDatabase, TenantEnvironmentKind.Erp);
            var betaProduction = RuntimeTestTenant.Create("beta", "beta.erpgenerator.example", betaProductionDatabase, TenantEnvironmentKind.Erp);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var reports = new RuntimeReportStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var compiler = new ErpBundleCompiler();
            var planner = new ErpBundleMigrationPlanner();
            var v1 = compiler.Compile(await new ErpBundleYamlLoader().LoadFileAsync(RuntimeBundleTestHelper.FindBundlePath("sample-erp.yaml")));
            var v2 = compiler.Compile(await new ErpBundleYamlLoader().LoadFileAsync(RuntimeBundleTestHelper.FindBundlePath("sample-erp-v2.yaml")));
            var v3 = compiler.Compile(await new ErpBundleYamlLoader().LoadFileAsync(RuntimeBundleTestHelper.FindBundlePath("sample-erp-v3.yaml")));
            var owner = new RuntimeUserContext(1, "owner@acme.test", "owner");
            var finance = new RuntimeUserContext(2, "finance@acme.test", "finance")
            {
                Roles = ["finance"]
            };

            await runtimeDatabase.ApplyBundleDeploymentAsync(acmeStaging, v1, planner.PlanMigration(null, v1), "erp", "staged", activate: true, "v1 staged");
            await runtimeDatabase.ApplyBundleDeploymentAsync(acmeProduction, v1, planner.PlanMigration(null, v1), "erp", "erp", activate: true, "v1 promoted");
            await runtimeDatabase.ApplyBundleDeploymentAsync(betaProduction, v1, planner.PlanMigration(null, v1), "erp", "erp", activate: true, "beta v1 promoted");

            var v2Plan = planner.PlanMigration(await runtimeDatabase.ReadActiveCompiledBundleAsync(acmeProduction), v2);
            v2Plan.RiskLevel.Should().Be("safe");
            await runtimeDatabase.ApplyBundleDeploymentAsync(acmeStaging, v2, v2Plan, "erp", "staged", activate: true, "v2 staged");
            await runtimeDatabase.ApplyBundleDeploymentAsync(acmeProduction, v2, v2Plan, "erp", "erp", activate: true, "v2 promoted");
            await runtimeDatabase.ApplyBundleDeploymentAsync(betaProduction, v2, v2Plan, "erp", "erp", activate: true, "beta v2 promoted");

            var customer = await records.CreateRecordAsync(acmeProduction, owner, "customers", RuntimeJson.JsonObject(
                ("company_name", $"Acme Customer {suffix}"),
                ("contact_email", $"acme-{suffix}@example.test"),
                ("status", "active")));
            var employee = await records.CreateRecordAsync(acmeProduction, owner, "employees", RuntimeJson.JsonObject(
                ("full_name", $"Acme Consultant {suffix}"),
                ("email", $"consultant-{suffix}@example.test"),
                ("role", "Consultant"),
                ("title", "Senior Consultant"),
                ("hourly_cost", 45),
                ("weekly_capacity_hours", 40)));
            var project = await records.CreateRecordAsync(acmeProduction, owner, "projects", RuntimeJson.JsonObject(
                ("customer_id", customer["id"]!),
                ("project_manager_id", employee["id"]!),
                ("title", $"Migration Project {suffix}"),
                ("budget", 1200),
                ("starts_on", "2026-07-03"),
                ("status", "active"),
                ("priority", "high")));
            var timesheet = await records.CreateRecordAsync(acmeProduction, owner, "timesheets", RuntimeJson.JsonObject(
                ("employee_id", employee["id"]!),
                ("project_id", project["id"]!),
                ("logged_on", "2026-07-03"),
                ("hours", 6.5),
                ("billable", true),
                ("billable_rate", 100),
                ("approval_status", "approved")));
            var invoice = await records.CreateRecordAsync(acmeProduction, owner, "invoices", RuntimeJson.JsonObject(
                ("customer_id", customer["id"]!),
                ("project_id", project["id"]!),
                ("invoice_number", $"INV-{suffix}"),
                ("issued_on", "2026-07-03"),
                ("amount", 1250),
                ("status", "sent"),
                ("due_date", "2026-07-20"),
                ("notes", "v2 baseline invoice")));

            var beforeV3 = await runtimeDatabase.ReadMetadataAsync(acmeProduction);
            beforeV3.BundleVersion.Should().Be("0.2.0");
            beforeV3.Entities.Should().NotContain(entity => entity.GetProperty("Id").GetString() == "expense");

            var v3Plan = planner.PlanMigration(await runtimeDatabase.ReadActiveCompiledBundleAsync(acmeProduction), v3);
            v3Plan.RiskLevel.Should().Be("warning",
                "the v2 to v3 semantic workflow and permission changes require explicit review");
            v3Plan.Operations.Should().Contain(operation => operation.Type == "add_entity" && operation.Entity == "expense");
            v3Plan.Operations.Should().Contain(operation => operation.Type == "add_entity" && operation.Entity == "invoice_line");
            v3Plan.Operations.Should().Contain(operation => operation.Type == "add_entity" && operation.Entity == "resource_allocation");
            v3Plan.Operations.Should().Contain(operation => operation.Type == "add_workflow" && operation.Field == "expense_approval");
            v3Plan.Operations.Should().Contain(operation => operation.Type == "add_workflow" && operation.Field == "invoice_approval");
            v3Plan.Operations.Should().Contain(operation => operation.Type == "add_report" && operation.Field == "expenses_by_project");
            await runtimeDatabase.ApplyBundleDeploymentAsync(acmeStaging, v3, v3Plan, "erp", "staged", activate: true, "v3 staged");
            await runtimeDatabase.ApplyBundleDeploymentAsync(acmeProduction, v3, v3Plan, "erp", "erp", activate: true, "v3 promoted");

            var afterV3 = await runtimeDatabase.ReadMetadataAsync(acmeProduction);
            afterV3.BundleVersion.Should().Be("0.3.0");
            afterV3.Entities.Should().Contain(entity => entity.GetProperty("Id").GetString() == "expense");
            afterV3.Entities.Should().Contain(entity => entity.GetProperty("Id").GetString() == "invoice_line");
            afterV3.Entities.Should().Contain(entity => entity.GetProperty("Id").GetString() == "resource_allocation");
            afterV3.Workflows.Should().Contain(workflow => workflow.GetProperty("Id").GetString() == "expense_approval");
            afterV3.Workflows.Should().Contain(workflow => workflow.GetProperty("Id").GetString() == "invoice_approval");
            afterV3.Reports.Should().Contain(report => report.GetProperty("Key").GetString() == "expenses_by_project");
            var v3ProjectMetadata = afterV3.Entities.Single(entity => entity.GetProperty("Id").GetString() == "project");
            v3ProjectMetadata.GetProperty("Fields").EnumerateArray().Should().Contain(field => field.GetProperty("Name").GetString() == "priority");
            v3ProjectMetadata.GetProperty("Fields").EnumerateArray().Should().Contain(field => field.GetProperty("Name").GetString() == "ends_on");
            (await records.ListRecordsAsync(acmeProduction, "projects", new RuntimeRecordQuery())).Records
                .Should().Contain(record => Convert.ToString(record["title"]) == $"Migration Project {suffix}");
            var betaDuringAcmeV3 = await runtimeDatabase.ReadMetadataAsync(betaProduction);
            betaDuringAcmeV3.BundleVersion.Should().Be("0.2.0");
            betaDuringAcmeV3.Entities.Should().NotContain(entity => entity.GetProperty("Id").GetString() == "expense");

            await records.UpdateRecordAsync(acmeProduction, owner, "employees", Convert.ToInt64(employee["id"]), RuntimeJson.JsonObject(("active", true)));
            var updated = await records.UpdateRecordAsync(acmeProduction, owner, "projects", Convert.ToInt64(project["id"]), RuntimeJson.JsonObject(("ends_on", "2026-08-03")));
            updated.Should().NotBeNull();
            updated!["ends_on"].Should().Be("2026-08-03");
            var updatedTimesheet = await records.UpdateRecordAsync(acmeProduction, owner, "timesheets", Convert.ToInt64(timesheet["id"]), RuntimeJson.JsonObject(
                ("invoice_id", invoice["id"]!),
                ("invoiced", true)));
            updatedTimesheet.Should().NotBeNull();
            updatedTimesheet!["invoice_id"].Should().Be(Convert.ToInt64(invoice["id"]));

            var invoiceLine = await records.CreateRecordAsync(acmeProduction, owner, "invoice_lines", RuntimeJson.JsonObject(
                ("invoice_id", invoice["id"]!),
                ("project_id", project["id"]!),
                ("timesheet_id", timesheet["id"]!),
                ("description", "Consulting hours"),
                ("quantity", 6.5),
                ("unit_price", 100)));
            invoiceLine["amount"].Should().Be(650m);
            var expense = await records.CreateRecordAsync(acmeProduction, owner, "expenses", RuntimeJson.JsonObject(
                ("employee_id", employee["id"]!),
                ("project_id", project["id"]!),
                ("date", "2026-07-03"),
                ("category", "software"),
                ("amount", 600),
                ("currency", "USD"),
                ("description", "Cloud tooling"),
                ("receipt_url", "https://example.test/receipt"),
                ("status", "draft")));
            await records.ArchiveRecordAsync(acmeProduction, owner, "expenses", Convert.ToInt64(expense["id"]), "archive/restore check");
            (await records.ListRecordsAsync(acmeProduction, "expenses", new RuntimeRecordQuery())).Records.Should().BeEmpty();
            (await records.ListRecordsAsync(acmeProduction, "expenses", new RuntimeRecordQuery(IncludeArchived: true))).Records.Should().ContainSingle();
            await records.RestoreRecordAsync(acmeProduction, owner, "expenses", Convert.ToInt64(expense["id"]));
            (await records.ListRecordsAsync(acmeProduction, "expenses", new RuntimeRecordQuery())).Records.Should().ContainSingle();
            var allocation = await records.CreateRecordAsync(acmeProduction, owner, "resource_allocations", RuntimeJson.JsonObject(
                ("employee_id", employee["id"]!),
                ("project_id", project["id"]!),
                ("start_date", "2026-07-01"),
                ("end_date", "2026-07-31"),
                ("allocation_percent", 75),
                ("role_on_project", "Consultant")));
            allocation["allocation_percent"].Should().Be(75m);

            var unpaidReport = await reports.GetReportAsync(acmeProduction, "unpaid_invoices");
            unpaidReport.Should().NotBeNull();
            var invoiceEntity = (await records.GetEntityAsync(acmeProduction, "invoices"))!;
            var unpaidResult = await reports.ExecuteReportAsync(acmeProduction, owner, unpaidReport!, invoiceEntity, new RuntimeReportQuery(), CancellationToken.None);
            unpaidResult.Rows.Single()["unpaid_amount"].Should().Be(1250m);

            var expenseStart = await workflows.StartActionAsync(acmeProduction, owner, "expenses", Convert.ToInt64(expense["id"]), "submit_for_approval");
            expenseStart.Task.Should().NotBeNull();
            expenseStart.Task!.AssignedRole.Should().Be("owner");
            var afterProjectManagerFallback = await workflows.ApproveTaskAsync(acmeProduction, owner, expenseStart.Task.Id);
            afterProjectManagerFallback.Instance.Status.Should().Be("waiting");
            var ownerExpenseTask = (await workflows.ListTasksAsync(acmeProduction, owner)).Should().ContainSingle(task =>
                task.InstanceId == expenseStart.Instance.Id &&
                task.StepId == "request_owner_expense_approval" &&
                task.Status == "pending").Subject;
            var approvedExpense = await workflows.ApproveTaskAsync(acmeProduction, owner, ownerExpenseTask.Id);
            approvedExpense.Instance.Status.Should().Be("completed");
            (await records.GetRecordAsync(acmeProduction, "expenses", Convert.ToInt64(expense["id"])))!["status"].Should().Be("approved");

            var invoiceStart = await workflows.StartActionAsync(acmeProduction, owner, "invoices", Convert.ToInt64(invoice["id"]), "submit_for_approval");
            invoiceStart.Task.Should().NotBeNull();
            invoiceStart.Task!.AssignedRole.Should().Be("finance");
            var approvedInvoice = await workflows.ApproveTaskAsync(acmeProduction, finance, invoiceStart.Task.Id);
            approvedInvoice.Instance.Status.Should().Be("completed");
            (await records.GetRecordAsync(acmeProduction, "invoices", Convert.ToInt64(invoice["id"])))!["status"].Should().Be("approved");

            var expensesReport = await reports.GetReportAsync(acmeProduction, "expenses_by_project");
            expensesReport.Should().NotBeNull();
            var expenseEntity = (await records.GetEntityAsync(acmeProduction, "expenses"))!;
            var expensesResult = await reports.ExecuteReportAsync(acmeProduction, owner, expensesReport!, expenseEntity, new RuntimeReportQuery(), CancellationToken.None);
            expensesResult.Rows.Should().ContainSingle();
            expensesResult.Rows.Single()["total_expenses"].Should().Be(600m);
            expensesResult.Rows.Single()["approved_expenses"].Should().Be(600m);

            var utilizationReport = await reports.GetReportAsync(acmeProduction, "employee_utilization");
            utilizationReport.Should().NotBeNull();
            var timesheetEntity = (await records.GetEntityAsync(acmeProduction, "timesheets"))!;
            var utilizationResult = await reports.ExecuteReportAsync(acmeProduction, owner, utilizationReport!, timesheetEntity, new RuntimeReportQuery(), CancellationToken.None);
            utilizationResult.Rows.Single()["billable_hours"].Should().Be(6.5m);
            utilizationResult.Rows.Single()["approved_hours"].Should().Be(6.5m);

            var profitabilityReport = await reports.GetReportAsync(acmeProduction, "project_profitability");
            profitabilityReport.Should().NotBeNull();
            var profitabilityResult = await reports.ExecuteReportAsync(acmeProduction, owner, profitabilityReport!, invoiceEntity, new RuntimeReportQuery(), CancellationToken.None);
            profitabilityResult.Rows.Single()["invoice_amount"].Should().Be(1250m);

            var allocationsReport = await reports.GetReportAsync(acmeProduction, "resource_allocations");
            allocationsReport.Should().NotBeNull();
            var allocationEntity = (await records.GetEntityAsync(acmeProduction, "resource_allocations"))!;
            var allocationResult = await reports.ExecuteReportAsync(acmeProduction, owner, allocationsReport!, allocationEntity, new RuntimeReportQuery(), CancellationToken.None);
            allocationResult.Rows.Single()["average_allocation"].Should().Be(75m);

            var dashboard = await reports.GetDashboardAsync(acmeProduction);
            dashboard.Widgets.Should().Contain(widget => widget.Key == "approved_expense_amount");
            dashboard.Widgets.Should().Contain(widget => widget.Key == "project_profitability_summary");
            dashboard.Widgets.Should().Contain(widget => widget.Key == "average_allocation");

            var rollback = await runtimeDatabase.RollbackActiveBundleAsync(acmeProduction);
            rollback.FromVersion.Should().Be("0.3.0");
            rollback.ToVersion.Should().Be("0.2.0");

            var afterRollback = await runtimeDatabase.ReadMetadataAsync(acmeProduction);
            afterRollback.BundleVersion.Should().Be("0.2.0");
            var rolledBackProjectMetadata = afterRollback.Entities.Single(entity => entity.GetProperty("Id").GetString() == "project");
            rolledBackProjectMetadata.GetProperty("Fields").EnumerateArray().Should().Contain(field => field.GetProperty("Name").GetString() == "priority");
            rolledBackProjectMetadata.GetProperty("Fields").EnumerateArray().Should().NotContain(field => field.GetProperty("Name").GetString() == "ends_on");
            afterRollback.Entities.Should().NotContain(entity => entity.GetProperty("Id").GetString() == "expense");
            afterRollback.Entities.Should().NotContain(entity => entity.GetProperty("Id").GetString() == "invoice_line");
            afterRollback.Entities.Should().NotContain(entity => entity.GetProperty("Id").GetString() == "resource_allocation");
            afterRollback.Workflows.Should().NotContain(workflow => workflow.GetProperty("Id").GetString() == "expense_approval");
            afterRollback.Workflows.Should().NotContain(workflow => workflow.GetProperty("Id").GetString() == "invoice_approval");
            afterRollback.Reports.Should().NotContain(report => report.GetProperty("Key").GetString() == "expenses_by_project");
            (await records.GetEntityAsync(acmeProduction, "expenses")).Should().BeNull();
            (await records.ListRecordsAsync(acmeProduction, "projects", new RuntimeRecordQuery())).Records
                .Should().Contain(record => Convert.ToString(record["title"]) == $"Migration Project {suffix}");
            var priorityColumnExists = await RuntimeDatabaseProbe.ColumnExistsAsync(factory, acmeProduction, "projects", "priority");
            priorityColumnExists.Should().BeTrue();
            (await RuntimeDatabaseProbe.ColumnExistsAsync(factory, acmeProduction, "projects", "ends_on")).Should().BeTrue();
            (await RuntimeDatabaseProbe.ColumnExistsAsync(factory, acmeProduction, "employees", "active")).Should().BeTrue();
            (await RuntimeDatabaseProbe.ColumnExistsAsync(factory, acmeProduction, "timesheets", "invoice_id")).Should().BeTrue();
            (await RuntimeDatabaseProbe.ColumnExistsAsync(factory, acmeProduction, "timesheets", "invoiced")).Should().BeTrue();
            (await RuntimeDatabaseProbe.TableExistsAsync(factory, acmeProduction, "expenses")).Should().BeTrue();
            (await RuntimeDatabaseProbe.TableExistsAsync(factory, acmeProduction, "invoice_lines")).Should().BeTrue();
            (await RuntimeDatabaseProbe.TableExistsAsync(factory, acmeProduction, "resource_allocations")).Should().BeTrue();

            var dangerousCompiled = ChangeCompiledFieldType(v2, "0.4.0", "invoice", "amount", "text");
            var blockedPlan = planner.PlanMigration(await runtimeDatabase.ReadActiveCompiledBundleAsync(acmeProduction), dangerousCompiled);
            blockedPlan.RiskLevel.Should().Be("blocked");
            var blockedDeployment = await runtimeDatabase.ApplyBundleDeploymentAsync(
                acmeProduction,
                dangerousCompiled,
                blockedPlan,
                "erp",
                "failed",
                activate: false,
                message: "dangerous v4 blocked");
            blockedDeployment.Status.Should().Be("failed");
            (await runtimeDatabase.ReadMetadataAsync(acmeProduction)).BundleVersion.Should().Be("0.2.0");

            var acmeHistory = await runtimeDatabase.ReadDeploymentHistoryAsync(acmeProduction);
            acmeHistory.Should().Contain(item => item.Status == "deployed" && item.BundleVersion == "0.3.0");
            acmeHistory.Should().Contain(item => item.Status == "deployed" && item.BundleVersion == "0.2.0");
            acmeHistory.Should().Contain(item => item.Status == "rolled_back");
            acmeHistory.Should().Contain(item => item.Status == "failed" && item.RiskLevel == "blocked");

            var betaMetadata = await runtimeDatabase.ReadMetadataAsync(betaProduction);
            betaMetadata.BundleVersion.Should().Be("0.2.0");
            var betaProjectMetadata = betaMetadata.Entities.Single(entity => entity.GetProperty("Id").GetString() == "project");
            betaProjectMetadata.GetProperty("Fields").EnumerateArray().Should().Contain(field => field.GetProperty("Name").GetString() == "priority");
            betaMetadata.Entities.Should().NotContain(entity => entity.GetProperty("Id").GetString() == "expense");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, acmeStagingDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, acmeProductionDatabase);
            await TestPostgres.DropDatabaseAsync(adminConnectionString, betaProductionDatabase);
        }
    }

    private static CompiledBundle ChangeCompiledFieldType(
        CompiledBundle bundle,
        string version,
        string entityId,
        string fieldId,
        string fieldType)
    {
        var entities = bundle.Entities
            .Select(entity => entity.Id == entityId
                ? entity with
                {
                    Fields = entity.Fields
                        .Select(field => field.Id == fieldId
                            ? field with { Type = fieldType, SqlType = fieldType }
                            : field)
                        .ToArray()
                }
                : entity)
            .ToArray();

        return bundle with
        {
            Version = version,
            BundleHash = new string('b', 64),
            Entities = entities
        };
    }

}
