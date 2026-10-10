using System.Text.Json;
using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Compilation.Hashing;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class BundleCompilerTests
{
    [Fact]
    public async Task Sample_bundle_compiles_to_runtime_metadata_and_app_tables()
    {
        var loader = new ErpBundleYamlLoader();
        var compiler = new ErpBundleCompiler();
        var bundle = await loader.LoadFileAsync(FindSampleBundlePath());

        var compiled = compiler.Compile(bundle);

        compiled.BundleId.Should().Be("sample_operations_erp");
        compiled.Entities.Select(entity => entity.TableName).Should().BeEquivalentTo(
            "customers",
            "employees",
            "projects",
            "timesheets",
            "invoices");
        compiled.Tables.Select(table => $"{table.SchemaName}.{table.TableName}").Should().Contain(
        [
            "app.customers",
            "app.employees",
            "app.projects",
            "app.timesheets",
            "app.invoices"
        ]);
        compiled.Views.Should().Contain(view => view.Id == "active_projects");
        compiled.Workflows.Should().Contain(workflow => workflow.Id == "timesheet_approval");
        compiled.Reports.Should().Contain(report => report.Key == "project_hours" && report.SourceEntity == "timesheet");
        compiled.Reports.Single(report => report.Key == "project_hours").GroupBy.Should().ContainSingle(group => group.Field == "project");
        compiled.Reports.Should().Contain(report => report.Key == "unpaid_invoices" && report.Metrics.Any(metric => metric.Key == "unpaid_amount"));
        compiled.Dashboard.Widgets.Should().Contain(widget => widget.Key == "billable_hours_this_month" && widget.Report == "project_hours");
        var timesheetWorkflow = compiled.Workflows.Single(workflow => workflow.Id == "timesheet_approval");
        timesheetWorkflow.Trigger!.Action.Should().Be("submit_for_approval");
        timesheetWorkflow.Steps.Should().Contain(step =>
            step.Id == "request_project_manager_approval" &&
            step.Type == "approval" &&
            step.AssignedTo!.Type == "relation_user" &&
            step.AssignedTo.Path == "project.project_manager" &&
            step.AssignedTo.RequiredRole == "member" &&
            step.AssignedTo.FallbackRole == "owner");
        var overtimeCondition = timesheetWorkflow.Steps.Single(step => step.Id == "overtime_owner_review");
        overtimeCondition.Type.Should().Be("condition");
        overtimeCondition.Condition!.Op.Should().Be("gt");
        overtimeCondition.Condition.Left!.Field.Should().Be("hours");
        overtimeCondition.Then.Should().ContainSingle(step => step.Id == "request_owner_overtime_approval" && step.Type == "approval");
        timesheetWorkflow.Steps.Should().Contain(step => step.Id == "mark_submitted" && step.Set["approval_status"] == "submitted");
        timesheetWorkflow.OnRejected.Should().Contain(step => step.Id == "mark_rejected" && step.Set["approval_status"] == "rejected");
        compiled.Roles.Should().Contain(role => role.Id == "owner" && role.Grants.Any(grant => grant.Action == "*" && grant.Entity == "*"));
        compiled.Roles.Single(role => role.Id == "admin").Grants.Should().Contain(grant => grant.Action == "approve_task" && grant.Entity == "*");
        compiled.Entities.Single(entity => entity.Id == "timesheet").Fields.Should().Contain(field => field.Name == "approval_status" && field.Type == "enum");
        compiled.Entities.Single(entity => entity.Id == "timesheet").Fields.Should().Contain(field =>
            field.Name == "billable_amount" &&
            field.Computed != null &&
            field.Computed.Op == "multiply");
        compiled.Tables.Single(table => table.TableName == "timesheets").Columns.Should().NotContain(column => column.Name == "billable_amount");
        compiled.Entities.Single(entity => entity.Id == "project").Fields.Should().Contain(field => field.Name == "project_manager_id" && field.References == "employee");
        compiled.Entities.Single(entity => entity.Id == "customer").Display.PrimaryField.Should().Be("company_name");
        compiled.BundleHash.Should().HaveLength(64);
    }

    [Fact]
    public async Task Migration_planner_marks_initial_sample_deployment_safe()
    {
        var bundle = await new ErpBundleYamlLoader().LoadFileAsync(FindSampleBundlePath());
        var compiled = new ErpBundleCompiler().Compile(bundle);

        var plan = new ErpBundleMigrationPlanner().PlanMigration(null, compiled);

        plan.RiskLevel.Should().Be("safe");
        plan.IsBlocked.Should().BeFalse();
        plan.Operations.Should().Contain(operation => operation.Type == "add_entity" && operation.Entity == "project");
        plan.Operations.Should().Contain(operation => operation.Type == "add_report" && operation.Field == "project_hours");
        plan.Operations.Should().Contain(operation => operation.Type == "add_dashboard_widget" && operation.Field == "billable_hours_this_month");
    }

    [Fact]
    public void Bundle_hash_canonicalizes_semantically_equal_numeric_scales_and_exponents()
    {
        var integralHash = HashWithActionAmount(10);
        var scaledDecimalHash = HashWithActionAmount(10.00m);
        var exponentHash = HashWithActionAmount(JsonNumber("1e1"));

        scaledDecimalHash.Should().Be(integralHash);
        exponentHash.Should().Be(integralHash);
    }

    [Fact]
    public void Bundle_hash_distinguishes_zero_from_values_below_decimal_precision()
    {
        HashWithActionAmount(0d).Should().NotBe(HashWithActionAmount(1e-29d));
    }

    [Fact]
    public void Bundle_hash_preserves_high_precision_json_numbers()
    {
        var highPrecision = JsonNumber("0.100000000000000000000000000001");

        HashWithActionAmount(highPrecision).Should().NotBe(HashWithActionAmount(0.1m));
    }

    [Theory]
    [InlineData("1e9999999999999999999999999999999999999999999")]
    [InlineData("1e-9999999999999999999999999999999999999999999")]
    public void Bundle_hash_supports_exponents_beyond_primitive_numeric_ranges(string rawNumber)
    {
        var hash = () => HashWithActionAmount(JsonNumber(rawNumber));

        hash.Should().NotThrow();
        hash().Should().HaveLength(64);
    }

    [Fact]
    public void Yaml_loader_preserves_high_precision_object_numbers_in_bundle_hashes()
    {
        var roundedHash = HashLoadedActionAmount("0.1");
        var highPrecisionHash = HashLoadedActionAmount("0.100000000000000000000000000001");

        highPrecisionHash.Should().NotBe(roundedHash);
    }

    [Fact]
    public void Yaml_loader_bundle_hash_distinguishes_zero_from_underflowing_single_values()
    {
        HashLoadedActionAmount("0").Should().NotBe(HashLoadedActionAmount("1e-50"));
    }

    [Theory]
    [InlineData("1e9999999999999999999999999999999999999999999")]
    [InlineData("1e-9999999999999999999999999999999999999999999")]
    public void Yaml_loader_bundle_hash_supports_exponents_beyond_primitive_numeric_ranges(
        string yamlScalar)
    {
        var hash = () => HashLoadedActionAmount(yamlScalar);

        hash.Should().NotThrow();
        hash().Should().HaveLength(64);
    }

    [Fact]
    public void Yaml_loader_bundle_hash_canonicalizes_equivalent_numeric_scale_and_exponent_forms()
    {
        var preciseHash = HashLoadedActionAmount("0.100000000000000000000000000001");
        var scaledHash = HashLoadedActionAmount("0.10000000000000000000000000000100");
        var exponentHash = HashLoadedActionAmount("1.00000000000000000000000000001e-1");

        scaledHash.Should().Be(preciseHash);
        exponentHash.Should().Be(preciseHash);
    }

    [Fact]
    public void Yaml_loader_only_replaces_plain_object_typed_numeric_scalars()
    {
        var bundle = new ErpBundleYamlLoader().Load(ActionAmountYaml(
            """
            precise:
              value: 0.100000000000000000000000000001
            quoted:
              value: "0.100000000000000000000000000001"
            empty:
              value: null
            enabled:
              value: true
            ordinary_integer:
              value: 8
            ordinary_decimal:
              value: 0.1
            """));

        bundle.Version.Should().Be("1.0.0");
        bundle.Actions[0].Steps[0].Values["precise"].Value.Should().BeOfType<JsonElement>()
            .Which.GetRawText().Should().Be("0.100000000000000000000000000001");
        bundle.Actions[0].Steps[0].Values["quoted"].Value.Should().Be(
            "0.100000000000000000000000000001");
        bundle.Actions[0].Steps[0].Values["empty"].Value.Should().BeNull();
        bundle.Actions[0].Steps[0].Values["enabled"].Value.Should().Be(true);
        bundle.Actions[0].Steps[0].Values["ordinary_integer"].Value.Should().Be(8);
        bundle.Actions[0].Steps[0].Values["ordinary_decimal"].Value.Should().Be(0.1m);
    }

    [Fact]
    public async Task Migration_planner_allows_sample_v2_additive_changes()
    {
        var loader = new ErpBundleYamlLoader();
        var compiler = new ErpBundleCompiler();
        var v1 = compiler.Compile(await loader.LoadFileAsync(FindSampleBundlePath()));
        var v2 = compiler.Compile(await loader.LoadFileAsync(FindBundlePath("sample-erp-v2.yaml")));

        var plan = new ErpBundleMigrationPlanner().PlanMigration(v1, v2);

        plan.RiskLevel.Should().Be("safe");
        plan.Operations.Should().Contain(operation => operation.Type == "add_field" && operation.Entity == "invoice" && operation.Field == "notes");
        plan.Operations.Should().Contain(operation => operation.Type == "add_field" && operation.Entity == "project" && operation.Field == "priority");
        plan.Operations.Should().Contain(operation => operation.Type == "add_report" && operation.Field == "overdue_invoices");
        plan.Operations.Should().Contain(operation => operation.Type == "add_dashboard_widget" && operation.Field == "overdue_invoice_count");
    }

    [Fact]
    public async Task Sample_v3_it_services_bundle_compiles_and_surfaces_semantic_update_warnings_from_v2()
    {
        var loader = new ErpBundleYamlLoader();
        var compiler = new ErpBundleCompiler();
        var v2 = compiler.Compile(await loader.LoadFileAsync(FindBundlePath("sample-erp-v2.yaml")));
        var v3 = compiler.Compile(await loader.LoadFileAsync(FindBundlePath("sample-erp-v3.yaml")));

        v3.Version.Should().Be("0.3.0");
        v3.Entities.Select(entity => entity.Id).Should().Contain(
        [
            "customer",
            "employee",
            "project",
            "timesheet",
            "invoice",
            "invoice_line",
            "expense",
            "resource_allocation"
        ]);
        v3.Tables.Select(table => $"{table.SchemaName}.{table.TableName}").Should().Contain(
        [
            "app.invoice_lines",
            "app.expenses",
            "app.resource_allocations"
        ]);
        v3.Tables.Single(table => table.TableName == "invoice_lines").Columns.Should().NotContain(column => column.Name == "amount");

        v3.Entities.Single(entity => entity.Id == "employee").Fields.Should().Contain(field => field.Id == "active" && field.Type == "boolean");
        v3.Entities.Single(entity => entity.Id == "project").Fields.Should().Contain(field => field.Id == "ends_on" && field.Type == "date");
        v3.Entities.Single(entity => entity.Id == "timesheet").Fields.Should().Contain(field => field.Id == "invoice" && field.References == "invoice");
        v3.Entities.Single(entity => entity.Id == "timesheet").Fields.Should().Contain(field => field.Id == "invoiced" && field.Type == "boolean");
        v3.Entities.Single(entity => entity.Id == "invoice").Fields.Single(field => field.Id == "status").Options
            .Should().Contain(["draft", "submitted", "approved", "sent", "paid", "overdue", "cancelled"]);
        var invoiceLineAmount = v3.Entities.Single(entity => entity.Id == "invoice_line").Fields.Single(field => field.Id == "amount");
        invoiceLineAmount.Computed.Should().NotBeNull();
        invoiceLineAmount.Computed!.Op.Should().Be("multiply");
        invoiceLineAmount.Computed.Left.Field.Should().Be("quantity");
        invoiceLineAmount.Computed.Right.Field.Should().Be("unit_price");

        v3.Workflows.Should().Contain(workflow => workflow.Id == "timesheet_approval");
        v3.Workflows.Should().Contain(workflow => workflow.Id == "expense_approval" && workflow.Entity == "expense");
        v3.Workflows.Should().Contain(workflow => workflow.Id == "invoice_approval" && workflow.Entity == "invoice");
        v3.Workflows.Single(workflow => workflow.Id == "expense_approval").Steps.Should().Contain(step =>
            step.Id == "request_project_manager_approval" &&
            step.AssignedTo!.Type == "relation_user" &&
            step.AssignedTo.Path == "project.project_manager" &&
            step.AssignedTo.RequiredRole == "project_manager" &&
            step.AssignedTo.FallbackRole == "owner");
        v3.Workflows.Single(workflow => workflow.Id == "invoice_approval").Steps.Should().Contain(step =>
            step.Id == "request_finance_approval" &&
            step.AssignedTo!.Role == "finance");

        v3.Reports.Should().Contain(report => report.Key == "employee_utilization" && report.SourceEntity == "timesheet");
        v3.Reports.Should().Contain(report => report.Key == "expenses_by_project" && report.SourceEntity == "expense");
        v3.Reports.Should().Contain(report => report.Key == "project_profitability" && report.SourceEntity == "invoice");
        v3.Reports.Should().Contain(report => report.Key == "unpaid_invoices" && report.SourceEntity == "invoice");
        v3.Reports.Should().Contain(report => report.Key == "resource_allocations" && report.SourceEntity == "resource_allocation");
        v3.Reports.Single(report => report.Key == "project_hours").Metrics.Should().Contain(metric => metric.Key == "non_billable_hours");
        v3.Dashboard.Widgets.Should().Contain(widget => widget.Key == "approved_expense_amount" && widget.Report == "approved_expenses");
        v3.Dashboard.Widgets.Should().Contain(widget => widget.Key == "project_profitability_summary" && widget.Report == "project_profitability");
        v3.Dashboard.Widgets.Should().Contain(widget => widget.Key == "average_allocation" && widget.Report == "resource_allocations");
        v3.Roles.Select(role => role.Id).Should().Contain(["owner", "admin", "member", "readonly", "read_only", "finance", "project_manager"]);

        var plan = new ErpBundleMigrationPlanner().PlanMigration(v2, v3);

        plan.RiskLevel.Should().Be("warning");
        plan.IsBlocked.Should().BeFalse();
        plan.Operations.Should().Contain(operation => operation.Type == "add_entity" && operation.Entity == "invoice_line");
        plan.Operations.Should().Contain(operation => operation.Type == "add_entity" && operation.Entity == "expense");
        plan.Operations.Should().Contain(operation => operation.Type == "add_entity" && operation.Entity == "resource_allocation");
        plan.Operations.Should().Contain(operation => operation.Type == "add_field" && operation.Entity == "employee" && operation.Field == "active");
        plan.Operations.Should().Contain(operation => operation.Type == "add_field" && operation.Entity == "project" && operation.Field == "ends_on");
        plan.Operations.Should().Contain(operation => operation.Type == "add_relation_field" && operation.Entity == "timesheet" && operation.Field == "invoice");
        plan.Operations.Should().Contain(operation => operation.Type == "add_field" && operation.Entity == "timesheet" && operation.Field == "invoiced");
        plan.Operations.Should().Contain(operation => operation.Type == "add_field" && operation.Entity == "invoice" && operation.Field == "status" && operation.To == "submitted");
        plan.Operations.Should().Contain(operation => operation.Type == "add_field" && operation.Entity == "invoice" && operation.Field == "status" && operation.To == "approved");
        plan.Operations.Should().Contain(operation => operation.Type == "add_workflow" && operation.Field == "expense_approval");
        plan.Operations.Should().Contain(operation => operation.Type == "add_workflow" && operation.Field == "invoice_approval");
        plan.Operations.Should().Contain(operation => operation.Type == "add_report" && operation.Field == "expenses_by_project");
        plan.Operations.Should().Contain(operation => operation.Type == "add_report" && operation.Field == "approved_expenses");
        plan.Operations.Should().Contain(operation => operation.Type == "add_report" && operation.Field == "resource_allocations");
        plan.Operations.Should().Contain(operation => operation.Type == "add_dashboard_widget" && operation.Field == "approved_expense_amount");
        plan.Operations.Should().Contain(operation => operation.Type == "add_dashboard_widget" && operation.Field == "average_allocation");
        plan.Operations.Should().Contain(operation => operation.Type == "add_permission" && operation.Field == "finance:read:*");
        plan.Operations.Should().Contain(operation => operation.Type == "add_permission" && operation.Field == "project_manager:read:*");
        plan.Operations.Should().Contain(operation =>
            operation.Risk == "warning" && operation.Type.StartsWith("update_", StringComparison.Ordinal));
        plan.Operations.Should().NotContain(operation => operation.Risk == "blocked");
    }

    [Fact]
    public async Task Sample_v4_action_bundle_compiles_and_migrates_safely_from_v3()
    {
        var loader = new ErpBundleYamlLoader();
        var compiler = new ErpBundleCompiler();
        var v3 = compiler.Compile(await loader.LoadFileAsync(FindBundlePath("sample-erp-v3.yaml")));
        var v4 = compiler.Compile(await loader.LoadFileAsync(FindBundlePath("sample-erp-v4.yaml")));

        v4.Version.Should().Be("0.4.0");
        var action = v4.Actions.Should().ContainSingle(candidate => candidate.Key == "create_invoice_draft").Subject;
        action.Entity.Should().Be("project");
        action.Type.Should().Be("record_action");
        action.Permissions.Should().Contain(requirement => requirement.Entity == "invoice" && requirement.Action == "create");
        action.Permissions.Should().Contain(requirement => requirement.Entity == "invoice_line" && requirement.Action == "create");
        action.Permissions.Should().Contain(requirement => requirement.Entity == "timesheet" && requirement.Action == "read");
        action.Permissions.Should().Contain(requirement => requirement.Entity == "timesheet" && requirement.Action == "update");
        action.Steps.Select(step => step.Type).Should().Contain([
            "query_records",
            "guard",
            "create_record",
            "create_records_for_each",
            "update_records",
            "audit_event",
            "return_result"
        ]);
        var timesheetFilters = action.Steps.Single(step => step.Id == "find_timesheets").Where;
        timesheetFilters.Any(filter =>
            filter.Field == "billable" &&
            filter.Op == "eq" &&
            filter.Value is true).Should().BeTrue();
        timesheetFilters.Any(filter =>
            filter.Field == "invoiced" &&
            filter.Op == "neq" &&
            filter.Value is true).Should().BeTrue();
        var createInvoice = action.Steps.Single(step => step.Id == "create_invoice");
        createInvoice.Values["amount"].Aggregate.Should().NotBeNull();
        createInvoice.Values["amount"].Aggregate!.Result.Should().Be("find_timesheets");
        createInvoice.Values["amount"].Aggregate!.Expression!.Op.Should().Be("multiply");
        action.Steps.Single(step => step.Id == "create_invoice_lines").ForEach!.Result.Should().Be("find_timesheets");
        action.Steps.Single(step => step.Id == "mark_timesheets_invoiced").RecordsFrom!.Result.Should().Be("find_timesheets");
        v4.Roles
            .Where(role => role.Grants.Any(grant =>
                grant.Scope == PermissionGrantScopes.Action &&
                grant.Action == GranularPermissionActions.Execute &&
                grant.Entity == "project" &&
                grant.Target == "create_invoice_draft"))
            .Select(role => role.Id)
            .Should().BeEquivalentTo("owner", "admin", "finance");
        v4.Roles.Single(role => role.Id == "finance").Grants.Should().Contain(grant =>
            grant.Scope == PermissionGrantScopes.Entity &&
            grant.Action == "update" &&
            grant.Entity == "timesheet");

        var plan = new ErpBundleMigrationPlanner().PlanMigration(v3, v4);

        plan.RiskLevel.Should().Be("safe");
        plan.Operations.Should().Contain(operation => operation.Type == "add_action" && operation.Entity == "project" && operation.Field == "create_invoice_draft");
        plan.Operations.Should().NotContain(operation => operation.Risk == "blocked");
    }

    [Fact]
    public void Compiler_canonicalizes_action_entity_aliases_for_runtime_metadata()
    {
        var bundle = ActionBundle();
        bundle.Actions[0] = ActionDefinitionForTest(
            entity: "customers",
            requires: new ErpActionRequires
            {
                Permissions =
                [
                    new ErpGrant { Entity = "customers", Action = "read" },
                    new ErpGrant { Entity = "invoices", Action = "create" }
                ]
            },
            steps:
            [
                new ErpActionStep
                {
                    Id = "find_customers",
                    Type = "query_records",
                    Entity = "customers"
                },
                new ErpActionStep
                {
                    Id = "create_invoice",
                    Type = "create_record",
                    Entity = "invoices",
                    Values = new Dictionary<string, ErpActionValue>
                    {
                        ["customer"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
                        ["amount"] = new() { Value = 10 }
                    }
                }
            ]);

        var compiler = new ErpBundleCompiler();
        compiler.Validate(bundle).Should().BeEmpty();
        var compiled = compiler.Compile(bundle);

        var compiledAction = compiled.Actions.Should().ContainSingle().Subject;
        compiledAction.Entity.Should().Be("customer");
        compiledAction.Permissions.Should().BeEquivalentTo(
        [
            new ActionPermissionRequirementDefinition("read", "customer"),
            new ActionPermissionRequirementDefinition("create", "invoice")
        ]);
        compiledAction.Steps.Single(step => step.Id == "find_customers").Entity.Should().Be("customer");
        compiledAction.Steps.Single(step => step.Id == "create_invoice").Entity.Should().Be("invoice");

        var actionUpsert = new RuntimeSqlBuilder().BuildDeploymentStatements(compiled)
            .Single(statement => statement.Contains("insert into _runtime.action_definitions", StringComparison.OrdinalIgnoreCase));
        actionUpsert.Should().Contain("'create_invoice', 'Create Invoice', 'customer', 'record_action'");
    }

    [Theory]
    [InlineData("unknown_entity")]
    [InlineData("unknown_query_field")]
    [InlineData("unknown_step_reference")]
    [InlineData("duplicate_step")]
    [InlineData("raw_sql_like")]
    [InlineData("missing_required_create_field")]
    [InlineData("invalid_permission")]
    public void Compiler_rejects_invalid_action_bundles(string mutation)
    {
        var bundle = ActionBundle();
        switch (mutation)
        {
            case "unknown_entity":
                bundle.Actions[0] = ActionDefinitionForTest(entity: "missing");
                break;
            case "unknown_query_field":
                var unknownFieldSteps = ActionStepsForTest();
                unknownFieldSteps[0] = QueryCustomersStep("missing_field");
                bundle.Actions[0] = ActionDefinitionForTest(steps: unknownFieldSteps);
                break;
            case "unknown_step_reference":
                var unknownStepSteps = ActionStepsForTest();
                unknownStepSteps[1] = CreateInvoiceStep(new Dictionary<string, ErpActionValue>
                    {
                        ["customer"] = new()
                        {
                            ValueFrom = new ErpActionValueFrom
                            {
                                Step = "missing_step",
                                Field = "id"
                            }
                        },
                        ["amount"] = new() { Value = 10 }
                    });
                bundle.Actions[0] = ActionDefinitionForTest(steps: unknownStepSteps);
                break;
            case "duplicate_step":
                var duplicateSteps = ActionStepsForTest();
                duplicateSteps[1] = CreateInvoiceStep(new Dictionary<string, ErpActionValue>
                {
                    ["customer"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
                    ["amount"] = new() { Value = 10 }
                }, id: duplicateSteps[0].Id);
                bundle.Actions[0] = ActionDefinitionForTest(steps: duplicateSteps);
                break;
            case "raw_sql_like":
                bundle.Actions[0] = ActionDefinitionForTest(label: "Drop table action");
                break;
            case "missing_required_create_field":
                var missingRequiredSteps = ActionStepsForTest();
                missingRequiredSteps[1] = CreateInvoiceStep(new Dictionary<string, ErpActionValue>
                    {
                        ["customer"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } }
                    });
                bundle.Actions[0] = ActionDefinitionForTest(steps: missingRequiredSteps);
                break;
            case "invalid_permission":
                bundle.Actions[0] = ActionDefinitionForTest(
                    requires: new ErpActionRequires
                    {
                        Permissions = [new ErpGrant { Entity = "invoice", Action = "execute_shell" }]
                    });
                break;
        }

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>();
    }

    [Fact]
    public void Migration_planner_allows_additive_entities()
    {
        var compiler = new ErpBundleCompiler();
        var v1 = compiler.Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
        ]));
        var next = MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
        ], version: "1.1.0");
        next.Modules[0].Entities.Add(new ErpEntity
        {
            Id = "invoice",
            Name = "invoices",
            DisplayName = "Invoices",
            Fields =
            [
                new ErpField { Id = "number", Name = "number", DisplayName = "Number", Type = "string", Required = true }
            ]
        });
        var v2 = compiler.Compile(next);

        var plan = new ErpBundleMigrationPlanner().PlanMigration(v1, v2);

        plan.RiskLevel.Should().Be("safe");
        plan.Operations.Should().Contain(operation => operation.Type == "add_entity" && operation.Entity == "invoice");
    }

    [Fact]
    public void Migration_planner_blocks_new_required_fields_without_default()
    {
        var compiler = new ErpBundleCompiler();
        var v1 = compiler.Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
        ]));
        var v2 = compiler.Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
            new ErpField { Id = "external_id", Name = "external_id", DisplayName = "External ID", Type = "string", Required = true }
        ], version: "1.1.0"));

        var plan = new ErpBundleMigrationPlanner().PlanMigration(v1, v2);

        plan.RiskLevel.Should().Be("blocked");
        plan.Operations.Should().Contain(operation => operation.Type == "blocked_required_field_without_default" && operation.Field == "external_id");
    }

    [Fact]
    public void Migration_planner_deprecates_removed_fields_without_physical_drop()
    {
        var compiler = new ErpBundleCompiler();
        var v1 = compiler.Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
            new ErpField { Id = "notes", Name = "notes", DisplayName = "Notes", Type = "text" }
        ]));
        var v2 = compiler.Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
        ], version: "1.1.0"));

        var plan = new ErpBundleMigrationPlanner().PlanMigration(v1, v2);

        plan.RiskLevel.Should().Be("warning");
        plan.Operations.Should().Contain(operation => operation.Type == "deprecate_field" && operation.Field == "notes");
        plan.Operations.Single(operation => operation.Type == "deprecate_field").Rollbackable.Should().BeTrue();
    }

    [Fact]
    public void Migration_planner_blocks_destructive_field_changes()
    {
        var compiler = new ErpBundleCompiler();
        var v1 = compiler.Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = false }
        ]));
        var v2 = compiler.Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "number", Required = true }
        ], version: "1.1.0"));

        var plan = new ErpBundleMigrationPlanner().PlanMigration(v1, v2);

        plan.RiskLevel.Should().Be("blocked");
        plan.Operations.Should().Contain(operation => operation.Type == "blocked_change_field_type" && operation.Field == "name");
        plan.Operations.Should().Contain(operation => operation.Type == "blocked_required_field_without_default" && operation.Field == "name");
    }

    [Fact]
    public void Migration_planner_blocks_relation_target_changes()
    {
        var compiler = new ErpBundleCompiler();
        var v1 = compiler.Compile(RelationBundle("customer"));
        var v2 = compiler.Compile(RelationBundle("vendor", version: "1.1.0"));

        var plan = new ErpBundleMigrationPlanner().PlanMigration(v1, v2);

        plan.RiskLevel.Should().Be("blocked");
        plan.Operations.Should().Contain(operation => operation.Type == "blocked_relation_target_change" && operation.Entity == "project" && operation.Field == "customer");
    }

    [Fact]
    public void Migration_planner_surfaces_same_key_behavior_changes()
    {
        var compiled = new ErpBundleCompiler().Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
        ]));
        var action = new ActionDefinition(
            "close_project",
            "Close project",
            "project",
            "transaction",
            null,
            [],
            [],
            []);
        var view = new ViewDefinition("projects", "core", "Projects", "project", ["name"]);
        var workflow = new WorkflowDefinition(
            "project_approval",
            "core",
            "Project approval",
            "project",
            ["draft", "approved"],
            null,
            [],
            []);
        var report = new ReportDefinition(
            "project_count",
            "Project count",
            null,
            "project",
            [],
            [new ReportMetricDefinition("count", "Count", "count", null, null)],
            []);
        var widget = new DashboardWidgetDefinition("project_count", "Project count", "metric", "project_count", "count");
        var before = compiled with
        {
            Actions = [action],
            Views = [view],
            Workflows = [workflow],
            Reports = [report],
            Dashboard = new DashboardDefinition([widget])
        };
        var after = before with
        {
            Version = "1.1.0",
            Actions = [action with { Label = "Complete project" }],
            Views = [view with { Fields = ["name", "created_at"] }],
            Workflows = [workflow with { States = ["draft", "review", "approved"] }],
            Reports = [report with { Label = "Active project count" }],
            Dashboard = new DashboardDefinition([widget with { Label = "Active projects" }])
        };

        var plan = new ErpBundleMigrationPlanner().PlanMigration(before, after);

        plan.RiskLevel.Should().Be("warning");
        plan.Operations.Should().Contain(operation => operation.Type == "update_action" && operation.Field == "close_project");
        plan.Operations.Should().Contain(operation => operation.Type == "update_view" && operation.Field == "projects");
        plan.Operations.Should().Contain(operation => operation.Type == "update_workflow" && operation.Field == "project_approval");
        plan.Operations.Should().Contain(operation => operation.Type == "update_report" && operation.Field == "project_count");
        plan.Operations.Should().Contain(operation => operation.Type == "update_dashboard_widget" && operation.Field == "project_count");
    }

    [Fact]
    public void Migration_planner_blocks_physical_column_and_constraint_drift()
    {
        var compiled = new ErpBundleCompiler().Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
        ]));
        var previousEntity = compiled.Entities.Single();
        var previousField = previousEntity.Fields.Single(field => field.Id == "name");
        var changed = previousField with
        {
            ColumnName = "legal_name",
            Required = false,
            Unique = true
        };
        var to = compiled with
        {
            Version = "1.1.0",
            Entities = [previousEntity with { Fields = [changed] }]
        };

        var plan = new ErpBundleMigrationPlanner().PlanMigration(compiled, to);

        plan.RiskLevel.Should().Be("blocked");
        plan.Operations.Should().Contain(operation => operation.Type == "blocked_rename_field_column");
        plan.Operations.Should().Contain(operation => operation.Type == "blocked_make_field_optional_without_ddl");
        plan.Operations.Should().Contain(operation => operation.Type == "blocked_change_field_uniqueness");
    }

    [Fact]
    public void Migration_planner_warns_when_a_computed_expression_changes()
    {
        var compiled = new ErpBundleCompiler().Compile(MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
        ]));
        var previousEntity = compiled.Entities.Single();
        var previousField = previousEntity.Fields.Single(field => field.Id == "name");
        var changed = previousField with
        {
            Computed = new ComputedFieldDefinition(
                "add",
                new ComputedOperandDefinition(null, 1),
                new ComputedOperandDefinition(null, 2))
        };
        var to = compiled with
        {
            Version = "1.1.0",
            Entities = [previousEntity with { Fields = [changed] }]
        };

        var plan = new ErpBundleMigrationPlanner().PlanMigration(compiled, to);

        plan.RiskLevel.Should().Be("warning");
        plan.Operations.Should().Contain(operation => operation.Type == "update_computed_field" && operation.Field == "name");
    }

    [Fact]
    public void Compiler_rejects_sql_like_bundle_content()
    {
        var bundle = new ErpBundle
        {
            Id = "bad_bundle",
            Name = "Bad drop table bundle",
            Version = "0.1.0",
            Modules =
            [
                new ErpModule
                {
                    Id = "main",
                    Name = "Main",
                    Entities =
                    [
                        new ErpEntity
                        {
                            Id = "customer",
                            Name = "customers",
                            DisplayName = "Customers",
                            Fields =
                            [
                                new ErpField
                                {
                                    Id = "name",
                                    Name = "name",
                                    DisplayName = "Name",
                                    Type = "string",
                                    Required = true
                                }
                            ]
                        }
                    ]
                }
            ],
            Roles = ExplicitOwnerRole()
        };

        var compiler = new ErpBundleCompiler();

        var act = () => compiler.Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("SQL-like", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_allows_generated_declarative_description_with_semicolon()
    {
        var bundle = MinimalBundle(
        [
            new ErpField
            {
                Id = "name",
                Name = "name",
                DisplayName = "Name",
                Type = "string",
                Required = true
            }
        ],
        description:
            "Single-site UK made-to-order furniture manufacturing ERP covering quotation, order release, procurement, inventory, production, shop-floor reporting, delivery, invoicing, job costing, month-end control, and management reporting. Xero remains the statutory ledger and payment-reconciliation system; SharePoint remains read-only document evidence storage.");

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().NotThrow();
    }

    [Fact]
    public void Sql_builder_creates_runtime_schemas_and_deterministic_app_table_statements()
    {
        var bundle = new ErpBundle
        {
            Id = "test_bundle",
            Name = "Test bundle",
            Version = "1.0.0",
            Modules =
            [
                new ErpModule
                {
                    Id = "crm",
                    Name = "CRM",
                    Entities =
                    [
                        new ErpEntity
                        {
                            Id = "customer",
                            Name = "customers",
                            DisplayName = "Customers",
                            Fields =
                            [
                                new ErpField
                                {
                                    Id = "company_name",
                                    Name = "company_name",
                                    DisplayName = "Company name",
                                    Type = "string",
                                    Required = true,
                                    Unique = true
                                }
                            ]
                        }
                    ]
                }
            ],
            Roles = ExplicitOwnerRole()
        };

        var compiled = new ErpBundleCompiler().Compile(bundle);
        var statements = new RuntimeSqlBuilder().BuildDeploymentStatements(compiled);

        statements.Should().Contain("create schema if not exists _runtime");
        statements.Should().Contain(statement => statement.Contains("create table if not exists \"app\".\"customers\"", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("\"archived_at\" timestamptz", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("alter table \"app\".\"customers\" add column if not exists \"archived_at\" timestamptz", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("alter table \"app\".\"customers\" add column if not exists \"company_name\" varchar(500)", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("insert into _runtime.role_definitions", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("'_runtime_owner'", StringComparison.Ordinal) &&
                                                  statement.Contains("Tenant owner (system)", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("token.runtime_user_id is null", StringComparison.Ordinal) &&
                                                  statement.Contains("token.consumed_at is not null", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("create table if not exists _runtime.report_definitions", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("create table if not exists _runtime.dashboard_definitions", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("create table if not exists _runtime.workflow_instances", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("create table if not exists _runtime.workflow_tasks", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("create table if not exists _runtime.workflow_events", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("create table if not exists _runtime.notification_outbox", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("delete from _runtime.workflow_definitions", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("delete from _runtime.report_definitions", StringComparison.Ordinal));
        statements.Should().Contain(statement => statement.Contains("create unique index if not exists \"ux_customers_company_name\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_preserves_enum_options_in_runtime_metadata()
    {
        var bundle = MinimalBundle([
            new ErpField
            {
                Id = "status",
                Name = "status",
                DisplayName = "Status",
                Type = "enum",
                Required = true,
                Options = ["active", "inactive"]
            }
        ]);

        var compiled = new ErpBundleCompiler().Compile(bundle);

        var field = compiled.Entities.Single().Fields.Single();
        field.Type.Should().Be("enum");
        field.SqlType.Should().Be("varchar(160)");
        field.Options.Should().BeEquivalentTo("active", "inactive");
    }

    [Fact]
    public void Compiler_rejects_enum_fields_without_options()
    {
        var bundle = MinimalBundle([
            new ErpField
            {
                Id = "status",
                Name = "status",
                DisplayName = "Status",
                Type = "enum",
                Required = true
            }
        ]);

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("must declare at least one option", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_invalid_workflow_steps()
    {
        var bundle = MinimalBundle([
            new ErpField
            {
                Id = "approval_status",
                Name = "approval_status",
                DisplayName = "Approval status",
                Type = "enum",
                Required = false,
                Options = ["draft", "submitted", "approved"]
            }
        ]);
        bundle.Modules[0].Workflows.Add(
            new ErpWorkflow
            {
                Id = "bad_workflow",
                Name = "Bad workflow",
                Entity = "customer",
                Trigger = new ErpWorkflowTrigger { Action = "submit" },
                Steps =
                [
                    new ErpWorkflowStep
                    {
                        Id = "bad_step",
                        Type = "update_record",
                        Set = new Dictionary<string, string>
                        {
                            ["approval_status"] = "dropped"
                        }
                    }
                ]
            });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("unsupported value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_preserves_custom_roles_without_adding_owner_or_defaults()
    {
        var bundle = MinimalBundle([new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }]);
        bundle.Roles.Clear();
        bundle.Roles.Add("quality_manager", new ErpRole
        {
            DisplayName = "Quality Manager",
            Grants = [new ErpGrant { Entity = "customer", Action = "read" }]
        });

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Roles.Should().ContainSingle();
        compiled.Roles.Single().Id.Should().Be("quality_manager");
        compiled.Roles.Single().Grants.Should().ContainSingle(grant =>
            grant.Entity == "customer" && grant.Action == "read");
    }

    [Fact]
    public void Compiler_rejects_bundle_without_explicit_roles()
    {
        var bundle = MinimalBundle([new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }]);
        bundle.Roles.Clear();

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("no default roles or owner grants are inferred", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_role_ids_that_collide_after_normalization()
    {
        var bundle = MinimalBundle([new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }]);
        bundle.Roles.Add("owner ", new ErpRole
        {
            DisplayName = "Duplicate owner",
            Grants = [new ErpGrant { Entity = "customer", Action = "read" }]
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("duplicate normalized role id 'owner'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_preserves_existing_custom_owner_definition()
    {
        var bundle = MinimalBundle([new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }]);
        bundle.Roles["owner"] = new ErpRole
        {
            DisplayName = "Custom Owner",
            Grants = [new ErpGrant { Entity = "customer", Action = "read" }]
        };

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Roles.Should().ContainSingle();
        compiled.Roles.Single().Id.Should().Be("owner");
        compiled.Roles.Single().DisplayName.Should().Be("Custom Owner");
        compiled.Roles.Single().Grants.Should().ContainSingle(grant =>
            grant.Entity == "customer" && grant.Action == "read");
    }

    [Fact]
    public void Compiler_rejects_reserved_runtime_owner_role()
    {
        var bundle = MinimalBundle([new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }]);
        bundle.Roles.Clear();
        bundle.Roles[RuntimeSystemRoleIds.TenantOwner] = new ErpRole
        {
            DisplayName = "Forged runtime owner",
            Grants = [new ErpGrant { Entity = "customer", Action = "read" }]
        };

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("reserved for tenant-local runtime bootstrap authority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_invalid_relation_assignment_paths()
    {
        var bundle = MinimalBundle(
        [
            new ErpField
            {
                Id = "project",
                Name = "project_id",
                DisplayName = "Project",
                Type = "relation",
                Required = true,
                References = "project"
            }
        ]);
        bundle.Modules[0].Entities.Add(new ErpEntity
        {
            Id = "project",
            Name = "projects",
            DisplayName = "Projects",
            Fields =
            [
                new ErpField
                {
                    Id = "title",
                    Name = "title",
                    DisplayName = "Title",
                    Type = "string",
                    Required = true
                }
            ]
        });
        bundle.Modules[0].Workflows.Add(new ErpWorkflow
        {
            Id = "bad_assignment",
            Name = "Bad Assignment",
            Entity = "customer",
            Trigger = new ErpWorkflowTrigger { Action = "submit" },
            Steps =
            [
                new ErpWorkflowStep
                {
                    Id = "approval",
                    Type = "approval",
                    AssignedTo = new ErpWorkflowAssignment
                    {
                        Type = "relation_user",
                        Path = "project.project_manager",
                        RequiredRole = "member",
                        FallbackRole = "owner"
                    }
                }
            ]
        });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("assignment path segment 'project_manager'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Compiler_rejects_relation_assignment_without_required_role()
    {
        var bundle = await new ErpBundleYamlLoader().LoadFileAsync(FindSampleBundlePath());
        var workflow = bundle.Modules.SelectMany(module => module.Workflows)
            .Single(item => item.Id == "timesheet_approval");
        var stepIndex = workflow.Steps.FindIndex(step =>
            step.AssignedTo.Type == "relation_user");
        var original = workflow.Steps[stepIndex];
        workflow.Steps[stepIndex] = new ErpWorkflowStep
        {
            Id = original.Id,
            Type = original.Type,
            Label = original.Label,
            AssignedTo = new ErpWorkflowAssignment
            {
                Type = "relation_user",
                Path = original.AssignedTo.Path,
                FallbackRole = original.AssignedTo.FallbackRole
            },
            ApproveLabel = original.ApproveLabel,
            RejectLabel = original.RejectLabel
        };

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("must declare assignedTo.requiredRole", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_accepts_approve_only_role_assignment_with_exact_direct_linked_record_task_authority()
    {
        var bundle = RoleAssignmentAuthorityBundle(
        [
            new ErpGrant { Action = "read", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "read_task", Entity = "warehouse_transfer" },
            RoleDirectRecordGrant("approve_task")
        ]);

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().Steps.Single().AssignedTo.Should().Match<WorkflowAssignmentDefinition>(assignment =>
            assignment.Type == "role" &&
            assignment.Role == "warehouse_user");
    }

    [Fact]
    public void Compiler_accepts_approve_only_role_assignment_with_exact_flat_task_authority()
    {
        var bundle = RoleAssignmentAuthorityBundle(
        [
            new ErpGrant { Action = "read", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "read_task", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "approve_task", Entity = "warehouse_transfer" }
        ]);

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().Steps.Single().AssignedTo!.Role.Should().Be("warehouse_user");
    }

    [Fact]
    public void Compiler_requires_exact_reject_authority_when_role_workflow_has_a_rejection_branch()
    {
        var bundle = RoleAssignmentAuthorityBundle(
        [
            new ErpGrant { Action = "read", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "read_task", Entity = "warehouse_transfer" },
            RoleDirectRecordGrant("approve_task")
        ]);
        bundle.Modules[0].Workflows[0].OnRejected.Add(new ErpWorkflowStep
        {
            Id = "mark_rejected",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["reference"] = "rejected" }
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("warehouse_transfer:reject_task", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_accepts_direct_linked_reject_authority_for_a_rejection_branch()
    {
        var bundle = RoleAssignmentAuthorityBundle(
        [
            new ErpGrant { Action = "read", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "read_task", Entity = "warehouse_transfer" },
            RoleDirectRecordGrant("approve_task"),
            RoleDirectRecordGrant("reject_task")
        ]);
        bundle.Modules[0].Workflows[0].OnRejected.Add(new ErpWorkflowStep
        {
            Id = "mark_rejected",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["reference"] = "rejected" }
        });

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().OnRejected.Should().ContainSingle();
    }

    [Fact]
    public void Compiler_accepts_terminal_rejection_branch_steps()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        bundle.Modules[0].Workflows[0].OnRejected.AddRange(
        [
            new ErpWorkflowStep
            {
                Id = "mark_rejected",
                Type = "update_record",
                Set = new Dictionary<string, string> { ["reference"] = "rejected" }
            },
            new ErpWorkflowStep
            {
                Id = "audit_rejected",
                Type = "audit_event",
                Event = "warehouse_transfer.rejected",
                Message = "Warehouse transfer rejected."
            },
            new ErpWorkflowStep
            {
                Id = "notify_rejected",
                Type = "notification_placeholder",
                Message = "Warehouse transfer needs changes."
            }
        ]);

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().OnRejected.Select(step => step.Type).Should().Equal(
            "update_record",
            "audit_event",
            "notification_placeholder");
    }

    [Theory]
    [InlineData("integer", "1.5", "incompatible value")]
    [InlineData("integer", "9223372036854775808", "incompatible value")]
    [InlineData("number", "not-a-number", "incompatible value")]
    [InlineData("decimal", "not-a-decimal", "incompatible value")]
    [InlineData("money", "not-money", "incompatible value")]
    [InlineData("boolean", "1", "incompatible value")]
    [InlineData("date", "2026-02-30", "incompatible value")]
    [InlineData("datetime", "yesterday", "incompatible value")]
    [InlineData("relation", "0", "positive integer identifier")]
    [InlineData("relation", "-1", "positive integer identifier")]
    [InlineData("relation", "1.5", "positive integer identifier")]
    [InlineData("enum", "missing", "unsupported value")]
    public void Compiler_rejects_runtime_incompatible_workflow_update_values_in_main_and_rejection_branches(
        string fieldType,
        string value,
        string expectedError)
    {
        foreach (var rejectionBranch in new[] { false, true })
        {
            var bundle = RoleAssignmentRejectionBranchBundle();
            var entity = bundle.Modules[0].Entities.Single(candidate => candidate.Id == "warehouse_transfer");
            entity.Fields.Add(WorkflowValueField(fieldType));
            var step = AddWorkflowValueUpdate(bundle, value, rejectionBranch);

            var compile = () => new ErpBundleCompiler().Compile(bundle);

            compile.Should().Throw<BundleCompilationException>()
                .Which.Errors.Should().Contain(error =>
                    error.Contains(step.Id, StringComparison.Ordinal) &&
                    error.Contains("warehouse_transfer.workflow_value", StringComparison.OrdinalIgnoreCase) &&
                    error.Contains(expectedError, StringComparison.OrdinalIgnoreCase),
                    $"{fieldType} value '{value}' must fail closed in {(rejectionBranch ? "on_rejected" : "main workflow")} steps");
        }
    }

    [Theory]
    [InlineData("string", "Runtime value")]
    [InlineData("text", "Runtime text")]
    [InlineData("integer", "42")]
    [InlineData("number", "12.50")]
    [InlineData("decimal", "12.50")]
    [InlineData("money", "1,234.50")]
    [InlineData("boolean", "TRUE")]
    [InlineData("date", "2026-07-12")]
    [InlineData("datetime", "2026-07-12T10:30:00+03:00")]
    [InlineData("relation", "1")]
    [InlineData("enum", "OPEN")]
    public void Compiler_accepts_runtime_parseable_workflow_update_values_in_main_and_rejection_branches(
        string fieldType,
        string value)
    {
        foreach (var rejectionBranch in new[] { false, true })
        {
            var bundle = RoleAssignmentRejectionBranchBundle();
            var entity = bundle.Modules[0].Entities.Single(candidate => candidate.Id == "warehouse_transfer");
            entity.Fields.Add(WorkflowValueField(fieldType));
            AddWorkflowValueUpdate(bundle, value, rejectionBranch);

            var compiled = new ErpBundleCompiler().Compile(bundle);

            var workflow = compiled.Workflows.Single();
            (rejectionBranch ? workflow.OnRejected : workflow.Steps)
                .Should().Contain(step => step.Set.ContainsKey("workflow_value"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Compiler_rejects_blank_or_null_required_workflow_updates_in_main_and_rejection_branches(
        string? value)
    {
        foreach (var rejectionBranch in new[] { false, true })
        {
            var bundle = RoleAssignmentRejectionBranchBundle();
            var step = AddWorkflowValueUpdate(bundle, value!, rejectionBranch, field: "reference");

            var compile = () => new ErpBundleCompiler().Compile(bundle);

            compile.Should().Throw<BundleCompilationException>()
                .Which.Errors.Should().Contain(error =>
                    error.Contains(step.Id, StringComparison.Ordinal) &&
                    error.Contains("required field 'warehouse_transfer.reference'", StringComparison.OrdinalIgnoreCase),
                    $"required null/blank updates must fail closed in {(rejectionBranch ? "on_rejected" : "main workflow")} steps");
        }
    }

    [Fact]
    public void Compiler_accepts_blank_main_workflow_update_for_an_optional_field_as_runtime_null()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        var entity = bundle.Modules[0].Entities.Single(candidate => candidate.Id == "warehouse_transfer");
        entity.Fields.Add(WorkflowValueField("integer"));
        var step = AddWorkflowValueUpdate(bundle, "   ", rejectionBranch: false);

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().Steps.Single(candidate => candidate.Id == step.Id)
            .Set["workflow_value"].Should().BeEmpty();
    }

    [Fact]
    public void Compiler_rejects_blank_rejection_update_for_an_optional_field_as_unprovable_runtime_null()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        var entity = bundle.Modules[0].Entities.Single(candidate => candidate.Id == "warehouse_transfer");
        entity.Fields.Add(WorkflowValueField("integer"));
        var step = AddWorkflowValueUpdate(bundle, "   ", rejectionBranch: true);

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains($"rejection branch step '{step.Id}'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("cannot clear optional field 'warehouse_transfer.workflow_value'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("exact rejection evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_blank_named_action_lifecycle_updates_in_main_and_rejection_branches()
    {
        foreach (var rejectionBranch in new[] { false, true })
        {
            var bundle = RoleAssignmentRejectionBranchBundle();
            var entity = bundle.Modules[0].Entities.Single(candidate => candidate.Id == "warehouse_transfer");
            entity.Fields.Add(WorkflowValueField("enum", namedActionLifecycle: true));
            var step = AddWorkflowValueUpdate(bundle, "   ", rejectionBranch);

            var compile = () => new ErpBundleCompiler().Compile(bundle);

            compile.Should().Throw<BundleCompilationException>()
                .Which.Errors.Should().Contain(error =>
                    error.Contains(step.Id, StringComparison.Ordinal) &&
                    error.Contains(
                        "cannot set named-action lifecycle field 'warehouse_transfer.workflow_value'",
                        StringComparison.OrdinalIgnoreCase),
                    $"blank lifecycle clears must fail closed in {(rejectionBranch ? "on_rejected" : "main workflow")} steps");
        }
    }

    [Fact]
    public void Compiler_rejects_computed_workflow_updates_in_main_and_rejection_branches()
    {
        foreach (var rejectionBranch in new[] { false, true })
        {
            var bundle = RoleAssignmentRejectionBranchBundle();
            var entity = bundle.Modules[0].Entities.Single(candidate => candidate.Id == "warehouse_transfer");
            entity.Fields.Add(new ErpField
            {
                Id = "workflow_value",
                Name = "workflow_value",
                DisplayName = "Workflow value",
                Type = "number",
                Computed = new ErpComputedField
                {
                    Op = "multiply",
                    Left = new ErpComputedOperand { Field = "owner_principal" },
                    Right = new ErpComputedOperand { Value = 2 }
                }
            });
            var step = AddWorkflowValueUpdate(bundle, "84", rejectionBranch);

            var compile = () => new ErpBundleCompiler().Compile(bundle);

            compile.Should().Throw<BundleCompilationException>()
                .Which.Errors.Should().Contain(error =>
                    error.Contains(step.Id, StringComparison.Ordinal) &&
                    error.Contains("cannot set computed field 'warehouse_transfer.workflow_value'", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Compiler_rejects_ignored_payload_on_terminal_rejection_steps()
    {
        var payloadMutations = new (string Payload, ErpWorkflowStep Step)[]
        {
            ("label", new ErpWorkflowStep
            {
                Id = "smuggle_label",
                Type = "update_record",
                Label = "Ignored label",
                Set = new Dictionary<string, string> { ["reference"] = "rejected" }
            }),
            ("assignedTo", new ErpWorkflowStep
            {
                Id = "smuggle_assignment",
                Type = "notification_placeholder",
                AssignedTo = new ErpWorkflowAssignment { Type = "role", Role = "warehouse_user" },
                Message = "Warehouse transfer needs changes."
            }),
            ("approveLabel", new ErpWorkflowStep
            {
                Id = "smuggle_approve_label",
                Type = "update_record",
                ApproveLabel = "Approve anyway",
                Set = new Dictionary<string, string> { ["reference"] = "rejected" }
            }),
            ("rejectLabel", new ErpWorkflowStep
            {
                Id = "smuggle_reject_label",
                Type = "update_record",
                RejectLabel = "Reject again",
                Set = new Dictionary<string, string> { ["reference"] = "rejected" }
            }),
            ("condition", new ErpWorkflowStep
            {
                Id = "smuggle_condition",
                Type = "update_record",
                Condition = new ErpWorkflowCondition { Op = "exists", Left = new ErpWorkflowConditionOperand { Field = "reference" } },
                Set = new Dictionary<string, string> { ["reference"] = "rejected" }
            }),
            ("if", new ErpWorkflowStep
            {
                Id = "smuggle_if",
                Type = "update_record",
                If = new ErpWorkflowCondition { Op = "exists", Left = new ErpWorkflowConditionOperand { Field = "reference" } },
                Set = new Dictionary<string, string> { ["reference"] = "rejected" }
            }),
            ("event", new ErpWorkflowStep
            {
                Id = "smuggle_update_event",
                Type = "update_record",
                Set = new Dictionary<string, string> { ["reference"] = "rejected" },
                Event = "warehouse_transfer.hidden_event"
            }),
            ("message", new ErpWorkflowStep
            {
                Id = "smuggle_update_message",
                Type = "update_record",
                Set = new Dictionary<string, string> { ["reference"] = "rejected" },
                Message = "This message is ignored by update_record."
            }),
            ("set", new ErpWorkflowStep
            {
                Id = "smuggle_audit_set",
                Type = "audit_event",
                Set = new Dictionary<string, string> { ["reference"] = "rejected" },
                Event = "warehouse_transfer.rejected"
            }),
            ("set", new ErpWorkflowStep
            {
                Id = "smuggle_notification_set",
                Type = "notification_placeholder",
                Set = new Dictionary<string, string> { ["reference"] = "rejected" },
                Message = "Warehouse transfer needs changes."
            }),
            ("event", new ErpWorkflowStep
            {
                Id = "smuggle_notification_event",
                Type = "notification_placeholder",
                Event = "warehouse_transfer.hidden_event",
                Message = "Warehouse transfer needs changes."
            })
        };

        foreach (var (payload, step) in payloadMutations)
        {
            var bundle = RoleAssignmentRejectionBranchBundle();
            bundle.Modules[0].Workflows[0].OnRejected.Add(step);

            var compile = () => new ErpBundleCompiler().Compile(bundle);

            compile.Should().Throw<BundleCompilationException>()
                .Which.Errors.Should().Contain(error =>
                    error.Contains($"rejection branch step '{step.Id}'", StringComparison.OrdinalIgnoreCase) &&
                    error.Contains($"'{payload}'", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Compiler_rejects_rejection_branch_without_a_reachable_main_approval()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        var workflow = bundle.Modules[0].Workflows[0];
        workflow.Steps.Clear();
        workflow.Steps.Add(new ErpWorkflowStep
        {
            Id = "mark_submitted",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["reference"] = "submitted" }
        });
        workflow.OnRejected.Add(new ErpWorkflowStep
        {
            Id = "mark_rejected",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["reference"] = "rejected" }
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("declares a rejection branch", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("no reachable approval step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_does_not_count_approval_nested_under_an_ignored_then_as_reachable()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        var workflow = bundle.Modules[0].Workflows[0];
        workflow.Steps.Clear();
        workflow.Steps.Add(new ErpWorkflowStep
        {
            Id = "mark_submitted",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["reference"] = "submitted" },
            Then =
            [
                new ErpWorkflowStep
                {
                    Id = "unreachable_approval",
                    Type = "approval",
                    AssignedTo = new ErpWorkflowAssignment { Type = "role", Role = "warehouse_user" }
                }
            ]
        });
        workflow.OnRejected.Add(new ErpWorkflowStep
        {
            Id = "mark_rejected",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["reference"] = "rejected" }
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("no reachable approval step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_accepts_rejection_branch_with_conditionally_reachable_main_approval()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        var workflow = bundle.Modules[0].Workflows[0];
        workflow.Steps.Clear();
        workflow.Steps.Add(new ErpWorkflowStep
        {
            Id = "needs_review",
            Type = "condition",
            Condition = new ErpWorkflowCondition
            {
                Op = "eq",
                Left = new ErpWorkflowConditionOperand { Field = "reference" },
                Right = new ErpWorkflowConditionOperand { Value = "submitted" }
            },
            Then =
            [
                new ErpWorkflowStep
                {
                    Id = "conditional_approval",
                    Type = "approval",
                    AssignedTo = new ErpWorkflowAssignment { Type = "role", Role = "warehouse_user" }
                }
            ]
        });
        workflow.OnRejected.Add(new ErpWorkflowStep
        {
            Id = "mark_rejected",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["reference"] = "rejected" }
        });

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().OnRejected.Should().ContainSingle();
    }

    [Fact]
    public void Compiler_rejects_approve_only_and_rejectable_workflows_with_one_start_identity()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        var original = bundle.Modules[0].Workflows[0];
        original.OnRejected.Add(new ErpWorkflowStep
        {
            Id = "mark_rejected",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["reference"] = "rejected" }
        });
        bundle.Modules[0].Workflows.Add(new ErpWorkflow
        {
            Id = "alternate_warehouse_transfer_review",
            Name = "Alternate warehouse transfer review",
            Entity = original.Entity,
            Trigger = new ErpWorkflowTrigger { Action = original.Trigger.Action },
            Steps =
            [
                new ErpWorkflowStep
                {
                    Id = "alternate_review",
                    Type = "approval",
                    AssignedTo = new ErpWorkflowAssignment { Type = "role", Role = "warehouse_user" }
                }
            ],
            OnRejected = []
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("workflow start identity must be unique", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("alternate_warehouse_transfer_review", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_rejects_top_level_approval_in_rejection_branch()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        bundle.Modules[0].Workflows[0].OnRejected.Add(new ErpWorkflowStep
        {
            Id = "rejection_approval",
            Type = "approval",
            AssignedTo = new ErpWorkflowAssignment { Type = "role", Role = "warehouse_user" }
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("rejection branch step 'rejection_approval'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("non-terminal type 'approval'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_nested_approval_in_rejection_branch()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        bundle.Modules[0].Workflows[0].OnRejected.Add(new ErpWorkflowStep
        {
            Id = "notify_reviewer",
            Type = "notification_placeholder",
            Then =
            [
                new ErpWorkflowStep
                {
                    Id = "nested_rejection_approval",
                    Type = "approval",
                    AssignedTo = new ErpWorkflowAssignment { Type = "role", Role = "warehouse_user" }
                }
            ]
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);
        var errors = compile.Should().Throw<BundleCompilationException>().Which.Errors;

        errors.Should().Contain(error =>
            error.Contains("rejection branch step 'notify_reviewer'", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("cannot declare nested then steps", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error =>
            error.Contains("rejection branch step 'nested_rejection_approval'", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("non-terminal type 'approval'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_condition_in_rejection_branch_even_when_its_effect_is_terminal()
    {
        var bundle = RoleAssignmentRejectionBranchBundle();
        bundle.Modules[0].Workflows[0].OnRejected.Add(new ErpWorkflowStep
        {
            Id = "conditional_rejection",
            Type = "condition",
            Condition = new ErpWorkflowCondition
            {
                Op = "eq",
                Left = new ErpWorkflowConditionOperand { Field = "reference" },
                Right = new ErpWorkflowConditionOperand { Value = "blocked" }
            },
            Then =
            [
                new ErpWorkflowStep
                {
                    Id = "mark_conditionally_rejected",
                    Type = "update_record",
                    Set = new Dictionary<string, string> { ["reference"] = "rejected" }
                }
            ]
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("rejection branch step 'conditional_rejection'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("non-terminal type 'condition'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_role_assignment_with_only_wildcard_task_authority()
    {
        var bundle = RoleAssignmentAuthorityBundle(
            [new ErpGrant { Action = "*", Entity = "*" }]);

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("assigned role 'warehouse_user'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("warehouse_transfer:read", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("record-scoped and wildcard grants cannot supply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_role_assignment_with_record_scoped_read_or_read_task()
    {
        var bundle = RoleAssignmentAuthorityBundle(
        [
            RoleDirectRecordGrant("read"),
            RoleDirectRecordGrant("read_task"),
            RoleDirectRecordGrant("approve_task")
        ]);

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        var errors = compile.Should().Throw<BundleCompilationException>().Which.Errors;
        errors.Should().Contain(error =>
            error.Contains("warehouse_transfer:read", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("exact flat entity-scope", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error =>
            error.Contains("warehouse_transfer:read_task", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("exact flat entity-scope", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_role_assignment_with_indirect_linked_record_task_authority()
    {
        var bundle = RoleAssignmentAuthorityBundle(
        [
            new ErpGrant { Action = "read", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "read_task", Entity = "warehouse_transfer" },
            RoleIndirectRecordGrant("approve_task")
        ]);

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        var errors = compile.Should().Throw<BundleCompilationException>().Which.Errors;
        errors.Should().Contain(error =>
            error.Contains("warehouse_transfer:approve_task", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("indirect linked_record", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_role_assignment_with_actor_principal_task_authority()
    {
        var bundle = RoleAssignmentAuthorityBundle(
        [
            new ErpGrant { Action = "read", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "read_task", Entity = "warehouse_transfer" },
            RoleActorPrincipalRecordGrant("approve_task")
        ]);

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("warehouse_transfer:approve_task", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("actor_principal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_two_hop_relation_assignment_with_only_intermediate_record_scope()
    {
        var bundle = RelationUserAuthorityBundle(
            "project.manager",
            DirectRecordApprovalGrants("project", "project"));

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("required role 'manager'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("request:approve_task", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("multi-segment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_accepts_two_hop_relation_assignment_with_exact_flat_task_authority()
    {
        var bundle = RelationUserAuthorityBundle(
            "project.manager",
            ExactFlatApprovalGrants());

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().Steps.Single().AssignedTo.Should().Match<WorkflowAssignmentDefinition>(assignment =>
            assignment.Type == "relation_user" &&
            assignment.Path == "project.manager" &&
            assignment.RequiredRole == "manager");
    }

    [Fact]
    public void Compiler_accepts_two_hop_direct_assignee_without_entity_read_only_with_closed_review_fields()
    {
        var bundle = RelationUserAuthorityBundle(
            "project.manager",
            [
                new ErpGrant { Action = "read_task", Entity = "request" },
                new ErpGrant { Action = "approve_task", Entity = "request" },
                FieldReadGrant("title"),
                FieldReadGrant("project")
            ],
            reviewFields: ["title", "project"]);

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().Steps.Single().ReviewFields.Should().Equal("title", "project");
    }

    [Fact]
    public void Compiler_rejects_missing_or_partially_readable_direct_assignee_review_fields()
    {
        var missingContract = RelationUserAuthorityBundle(
            "project.manager",
            [
                new ErpGrant { Action = "read_task", Entity = "request" },
                new ErpGrant { Action = "approve_task", Entity = "request" },
                FieldReadGrant("title")
            ]);
        var partialEntityReader = RelationUserAuthorityBundle(
            "project.manager",
            [
                new ErpGrant { Action = "read", Entity = "request" },
                new ErpGrant { Action = "read_task", Entity = "request" },
                new ErpGrant { Action = "approve_task", Entity = "request" },
                FieldReadGrant("title")
            ],
            reviewFields: ["title", "project"]);

        new Action(() => new ErpBundleCompiler().Compile(missingContract)).Should()
            .Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("non-empty exact reviewFields", StringComparison.Ordinal));
        new Action(() => new ErpBundleCompiler().Compile(partialEntityReader)).Should()
            .Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("request.project", StringComparison.Ordinal) &&
                error.Contains("field read authority", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_accepts_one_hop_relation_assignment_with_exact_direct_record_scope()
    {
        var bundle = RelationUserAuthorityBundle(
            "manager",
            DirectRecordApprovalGrants("manager", "employee"));

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().Steps.Single().AssignedTo.Should().Match<WorkflowAssignmentDefinition>(assignment =>
            assignment.Type == "relation_user" &&
            assignment.Path == "manager" &&
            assignment.RequiredRole == "manager");
    }

    [Fact]
    public void Compiler_rejects_relation_assignment_fallback_with_only_wildcard_authority()
    {
        var bundle = RelationUserAuthorityBundle(
            "manager",
            DirectRecordApprovalGrants("manager", "employee"),
            "fallback_reviewer",
            [new ErpGrant { Action = "*", Entity = "*" }]);

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("fallback role 'fallback_reviewer'", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("request:read", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("wildcard grants cannot supply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_accepts_relation_assignment_fallback_with_exact_flat_task_authority()
    {
        var bundle = RelationUserAuthorityBundle(
            "manager",
            DirectRecordApprovalGrants("manager", "employee"),
            "fallback_reviewer",
            ExactFlatApprovalGrants());

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().Steps.Single().AssignedTo!.FallbackRole.Should().Be("fallback_reviewer");
    }

    [Fact]
    public void Compiler_requires_exact_reject_authority_when_relation_workflow_has_a_rejection_branch()
    {
        var bundle = RelationUserAuthorityBundle(
            "manager",
            DirectRecordApprovalGrants("manager", "employee"),
            "fallback_reviewer",
            ExactFlatApprovalGrants());
        bundle.Modules[0].Workflows[0].OnRejected.Add(new ErpWorkflowStep
        {
            Id = "mark_rejected",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["title"] = "rejected" }
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);
        var errors = compile.Should().Throw<BundleCompilationException>().Which.Errors;

        errors.Should().Contain(error =>
            error.Contains("required role 'manager'", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("request:reject_task", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error =>
            error.Contains("fallback role 'fallback_reviewer'", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("request:reject_task", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_accepts_relation_rejection_branch_with_exact_required_and_fallback_authority()
    {
        var bundle = RelationUserAuthorityBundle(
            "manager",
            [.. DirectRecordApprovalGrants("manager", "employee"), DirectRecordGrant("reject_task", "manager", "employee")],
            "fallback_reviewer",
            [.. ExactFlatApprovalGrants(), new ErpGrant { Action = "reject_task", Entity = "request" }]);
        bundle.Modules[0].Workflows[0].OnRejected.Add(new ErpWorkflowStep
        {
            Id = "mark_rejected",
            Type = "update_record",
            Set = new Dictionary<string, string> { ["title"] = "rejected" }
        });

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Workflows.Single().OnRejected.Should().ContainSingle();
    }

    [Fact]
    public void Compiler_accepts_valid_condition_steps()
    {
        var bundle = BundleWithCondition(new ErpWorkflowCondition
        {
            Op = "gt",
            Left = new ErpWorkflowConditionOperand { Field = "hours" },
            Right = new ErpWorkflowConditionOperand { Value = 8 }
        });

        var compiled = new ErpBundleCompiler().Compile(bundle);

        var condition = compiled.Workflows.Single().Steps.Single(step => step.Type == "condition");
        condition.Condition!.Op.Should().Be("gt");
        condition.Then.Should().ContainSingle(step => step.Id == "owner_approval");
    }

    [Fact]
    public void Compiler_rejects_conditions_that_reference_unknown_fields()
    {
        var bundle = BundleWithCondition(new ErpWorkflowCondition
        {
            Op = "gt",
            Left = new ErpWorkflowConditionOperand { Field = "missing_hours" },
            Right = new ErpWorkflowConditionOperand { Value = 8 }
        });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("unknown field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_conditions_with_invalid_ops()
    {
        var bundle = BundleWithCondition(new ErpWorkflowCondition
        {
            Op = "exec",
            Left = new ErpWorkflowConditionOperand { Field = "hours" },
            Right = new ErpWorkflowConditionOperand { Value = 8 }
        });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("unsupported condition op", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_numeric_conditions_on_text_fields()
    {
        var bundle = BundleWithCondition(new ErpWorkflowCondition
        {
            Op = "gt",
            Left = new ErpWorkflowConditionOperand { Field = "name" },
            Right = new ErpWorkflowConditionOperand { Value = 8 }
        });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("requires numeric left field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_in_conditions_without_array_values()
    {
        var bundle = BundleWithCondition(new ErpWorkflowCondition
        {
            Op = "in",
            Left = new ErpWorkflowConditionOperand { Field = "hours" },
            Right = new ErpWorkflowConditionOperand { Value = 8 }
        });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("right.value to be an array", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_rejects_invalid_condition_relation_paths()
    {
        var fields = new List<ErpField>
        {
            new()
            {
                Id = "project",
                Name = "project_id",
                DisplayName = "Project",
                Type = "relation",
                Required = true,
                References = "project"
            },
            new()
            {
                Id = "hours",
                Name = "hours",
                DisplayName = "Hours",
                Type = "number",
                Required = true
            },
            new()
            {
                Id = "name",
                Name = "name",
                DisplayName = "Name",
                Type = "string",
                Required = true
            }
        };
        var bundle = BundleWithCondition(
            new ErpWorkflowCondition
            {
                Op = "exists",
                Left = new ErpWorkflowConditionOperand { Path = "project.missing_field" }
            },
            fields);
        bundle.Modules[0].Entities.Add(new ErpEntity
        {
            Id = "project",
            Name = "projects",
            DisplayName = "Projects",
            Fields =
            [
                new ErpField
                {
                    Id = "title",
                    Name = "title",
                    DisplayName = "Title",
                    Type = "string",
                    Required = true
                }
            ]
        });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("path segment 'missing_field'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_accepts_valid_report_and_computed_field()
    {
        var bundle = MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
            new ErpField { Id = "hours", Name = "hours", DisplayName = "Hours", Type = "number", Required = true },
            new ErpField { Id = "rate", Name = "rate", DisplayName = "Rate", Type = "money", Required = false },
            new ErpField
            {
                Id = "amount",
                Name = "amount",
                DisplayName = "Amount",
                Type = "money",
                Computed = new ErpComputedField
                {
                    Op = "multiply",
                    Left = new ErpComputedOperand { Field = "hours" },
                    Right = new ErpComputedOperand { Field = "rate" }
                }
            }
        ]);
        bundle.Reports.Add(new ErpReport
        {
            Key = "hours_by_name",
            Label = "Hours by name",
            Source = "customer",
            GroupBy = [new ErpReportGroupBy { Field = "name", Label = "Name" }],
            Metrics =
            [
                new ErpReportMetric { Key = "total_hours", Label = "Total hours", Op = "sum", Field = "hours" },
                new ErpReportMetric { Key = "total_amount", Label = "Total amount", Op = "sum", Field = "amount" }
            ]
        });

        var compiled = new ErpBundleCompiler().Compile(bundle);

        compiled.Reports.Should().ContainSingle(report => report.Key == "hours_by_name");
        compiled.Tables.Single().Columns.Should().NotContain(column => column.Name == "amount");
    }

    [Theory]
    [InlineData("missing_source")]
    [InlineData("unknown_metric_field")]
    [InlineData("invalid_metric_op")]
    [InlineData("text_sum")]
    [InlineData("sql_like")]
    public void Compiler_rejects_invalid_reports(string scenario)
    {
        var bundle = MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
            new ErpField { Id = "hours", Name = "hours", DisplayName = "Hours", Type = "number", Required = false }
        ]);
        bundle.Reports.Add(scenario switch
        {
            "missing_source" => new ErpReport
            {
                Key = "bad_report",
                Label = "Bad report",
                Source = "missing",
                Metrics = [new ErpReportMetric { Key = "total", Label = "Total", Op = "count" }]
            },
            "unknown_metric_field" => new ErpReport
            {
                Key = "bad_report",
                Label = "Bad report",
                Source = "customer",
                Metrics = [new ErpReportMetric { Key = "total", Label = "Total", Op = "sum", Field = "missing" }]
            },
            "invalid_metric_op" => new ErpReport
            {
                Key = "bad_report",
                Label = "Bad report",
                Source = "customer",
                Metrics = [new ErpReportMetric { Key = "total", Label = "Total", Op = "exec", Field = "hours" }]
            },
            "text_sum" => new ErpReport
            {
                Key = "bad_report",
                Label = "Bad report",
                Source = "customer",
                Metrics = [new ErpReportMetric { Key = "total", Label = "Total", Op = "sum", Field = "name" }]
            },
            _ => new ErpReport
            {
                Key = "bad_report",
                Label = "Bad SELECT * FROM customers report",
                Source = "customer",
                Metrics = [new ErpReportMetric { Key = "total", Label = "Total", Op = "count" }]
            }
        });

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>();
    }

    [Theory]
    [InlineData("unknown_ref")]
    [InlineData("invalid_op")]
    [InlineData("text_multiply")]
    public void Compiler_rejects_invalid_computed_fields(string scenario)
    {
        var computed = scenario switch
        {
            "unknown_ref" => new ErpComputedField
            {
                Op = "multiply",
                Left = new ErpComputedOperand { Field = "missing" },
                Right = new ErpComputedOperand { Value = 2 }
            },
            "invalid_op" => new ErpComputedField
            {
                Op = "exec",
                Left = new ErpComputedOperand { Field = "hours" },
                Right = new ErpComputedOperand { Value = 2 }
            },
            _ => new ErpComputedField
            {
                Op = "multiply",
                Left = new ErpComputedOperand { Field = "name" },
                Right = new ErpComputedOperand { Value = 2 }
            }
        };
        var bundle = MinimalBundle(
        [
            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
            new ErpField { Id = "hours", Name = "hours", DisplayName = "Hours", Type = "number", Required = false },
            new ErpField { Id = "amount", Name = "amount", DisplayName = "Amount", Type = "money", Computed = computed }
        ]);

        var act = () => new ErpBundleCompiler().Compile(bundle);

        act.Should().Throw<BundleCompilationException>();
    }

    private static string FindSampleBundlePath() => RuntimeBundleTestHelper.FindSampleBundlePath();

    private static string FindBundlePath(string fileName) => RuntimeBundleTestHelper.FindBundlePath(fileName);

    private static ErpBundle MinimalBundle(
        List<ErpField> fields,
        string version = "1.0.0",
        string description = "") => new()
    {
        Id = "test_bundle",
        Name = "Test bundle",
        Version = version,
        Description = description,
        Modules =
        [
            new ErpModule
            {
                Id = "crm",
                Name = "CRM",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "customer",
                        Name = "customers",
                        DisplayName = "Customers",
                        Fields = fields
                    }
                ]
            }
        ],
        Roles = ExplicitOwnerRole()
    };

    private static ErpBundle RelationBundle(string target, string version = "1.0.0") => new()
    {
        Id = "relation_bundle",
        Name = "Relation bundle",
        Version = version,
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
                        Id = "customer",
                        Name = "customers",
                        DisplayName = "Customers",
                        Fields = [new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }]
                    },
                    new ErpEntity
                    {
                        Id = "vendor",
                        Name = "vendors",
                        DisplayName = "Vendors",
                        Fields = [new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }]
                    },
                    new ErpEntity
                    {
                        Id = "project",
                        Name = "projects",
                        DisplayName = "Projects",
                        Fields =
                        [
                            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                            new ErpField { Id = "customer", Name = "customer_id", DisplayName = "Customer", Type = "relation", References = target }
                        ]
                    }
                ]
            }
        ],
        Roles = ExplicitOwnerRole()
    };

    private static Dictionary<string, ErpRole> ExplicitOwnerRole() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["owner"] = new ErpRole
        {
            DisplayName = "Owner",
            Grants = [new ErpGrant { Action = "*", Entity = "*" }]
        }
    };

    private static ErpBundle RelationUserAuthorityBundle(
        string path,
        IReadOnlyList<ErpGrant> requiredRoleGrants,
        string fallbackRole = "",
        IReadOnlyList<ErpGrant>? fallbackRoleGrants = null,
        IReadOnlyList<string>? reviewFields = null)
    {
        var roles = new Dictionary<string, ErpRole>(StringComparer.Ordinal)
        {
            ["manager"] = new ErpRole
            {
                DisplayName = "Manager",
                Grants = [.. requiredRoleGrants]
            }
        };
        if (!string.IsNullOrWhiteSpace(fallbackRole))
        {
            roles[fallbackRole] = new ErpRole
            {
                DisplayName = "Fallback reviewer",
                Grants = [.. fallbackRoleGrants ?? []]
            };
        }

        return new ErpBundle
        {
            Id = "relation_user_authority",
            Name = "Relation user authority",
            Version = "1.0.0",
            Modules =
            [
                new ErpModule
                {
                    Id = "operations",
                    Name = "Operations",
                    Entities =
                    [
                        new ErpEntity
                        {
                            Id = "request",
                            Name = "requests",
                            DisplayName = "Requests",
                            Fields =
                            [
                                TextField("title", "Title"),
                                RelationField("project", "Project", "project"),
                                RelationField("manager", "Manager", "employee")
                            ]
                        },
                        new ErpEntity
                        {
                            Id = "project",
                            Name = "projects",
                            DisplayName = "Projects",
                            Fields =
                            [
                                TextField("name", "Name"),
                                RelationField("manager", "Manager", "employee")
                            ]
                        },
                        new ErpEntity
                        {
                            Id = "employee",
                            Name = "employees",
                            DisplayName = "Employees",
                            Fields =
                            [
                                TextField("name", "Name"),
                                TextField("email", "Email")
                            ]
                        }
                    ],
                    Workflows =
                    [
                        new ErpWorkflow
                        {
                            Id = "request_approval",
                            Name = "Request approval",
                            Entity = "request",
                            Trigger = new ErpWorkflowTrigger { Action = "submit" },
                            Steps =
                            [
                                new ErpWorkflowStep
                                {
                                    Id = "manager_approval",
                                    Type = "approval",
                                    AssignedTo = new ErpWorkflowAssignment
                                    {
                                        Type = "relation_user",
                                        Path = path,
                                        RequiredRole = "manager",
                                        FallbackRole = fallbackRole
                                    },
                                    ReviewFields = [.. reviewFields ?? []]
                                }
                            ]
                        }
                    ]
                }
            ],
            Roles = roles
        };
    }

    private static ErpBundle RoleAssignmentAuthorityBundle(IReadOnlyList<ErpGrant> roleGrants) => new()
    {
        Id = "role_assignment_authority",
        Name = "Role assignment authority",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "inventory",
                Name = "Inventory",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "warehouse_transfer",
                        Name = "warehouse_transfers",
                        DisplayName = "Warehouse transfers",
                        Fields =
                        [
                            TextField("reference", "Reference"),
                            RelationField("destination_warehouse", "Destination warehouse", "warehouse"),
                            new ErpField
                            {
                                Id = "owner_principal",
                                Name = "owner_principal",
                                DisplayName = "Owner principal",
                                Type = "integer",
                                Required = true
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "warehouse",
                        Name = "warehouses",
                        DisplayName = "Warehouses",
                        Fields = [TextField("name", "Name")]
                    },
                    new ErpEntity
                    {
                        Id = "warehouse_user",
                        Name = "warehouse_users",
                        DisplayName = "Warehouse users",
                        Fields =
                        [
                            TextField("name", "Name"),
                            RelationField("warehouse", "Warehouse", "warehouse")
                        ]
                    }
                ],
                Workflows =
                [
                    new ErpWorkflow
                    {
                        Id = "transfer_receipt",
                        Name = "Transfer receipt",
                        Entity = "warehouse_transfer",
                        Trigger = new ErpWorkflowTrigger { Action = "record_destination_receipt" },
                        Steps =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "destination_receipt",
                                Type = "approval",
                                AssignedTo = new ErpWorkflowAssignment
                                {
                                    Type = "role",
                                    Role = "warehouse_user"
                                }
                            }
                        ]
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>(StringComparer.Ordinal)
        {
            ["warehouse_user"] = new ErpRole
            {
                DisplayName = "Warehouse user",
                Grants = [.. roleGrants]
            }
        }
    };

    private static ErpBundle RoleAssignmentRejectionBranchBundle() =>
        RoleAssignmentAuthorityBundle(
        [
            new ErpGrant { Action = "read", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "read_task", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "approve_task", Entity = "warehouse_transfer" },
            new ErpGrant { Action = "reject_task", Entity = "warehouse_transfer" }
        ]);

    private static ErpField WorkflowValueField(string fieldType, bool namedActionLifecycle = false) => new()
    {
        Id = "workflow_value",
        Name = fieldType.Equals("relation", StringComparison.OrdinalIgnoreCase)
            ? "workflow_value_id"
            : "workflow_value",
        DisplayName = "Workflow value",
        Type = fieldType,
        Required = false,
        References = fieldType.Equals("relation", StringComparison.OrdinalIgnoreCase)
            ? "warehouse"
            : null,
        Options = fieldType.Equals("enum", StringComparison.OrdinalIgnoreCase)
            ? ["open", "closed"]
            : [],
        Lifecycle = namedActionLifecycle
            ? new ErpFieldLifecycle { Initial = "open", Transitions = "named_action" }
            : null
    };

    private static ErpWorkflowStep AddWorkflowValueUpdate(
        ErpBundle bundle,
        string value,
        bool rejectionBranch,
        string field = "workflow_value")
    {
        var workflow = bundle.Modules[0].Workflows[0];
        var step = new ErpWorkflowStep
        {
            Id = rejectionBranch ? "set_rejected_workflow_value" : "set_main_workflow_value",
            Type = "update_record",
            Set = new Dictionary<string, string> { [field] = value }
        };
        if (rejectionBranch)
        {
            workflow.OnRejected.Add(step);
        }
        else
        {
            workflow.Steps.Insert(0, step);
        }

        return step;
    }

    private static ErpGrant RoleDirectRecordGrant(string action) => new()
    {
        Action = action,
        Entity = "warehouse_transfer",
        Scope = PermissionGrantScopes.Record,
        RecordScope = new ErpRecordScope
        {
            Field = "destination_warehouse",
            Source = RecordPermissionScopeSources.LinkedRecord,
            LinkedEntity = "warehouse"
        }
    };

    private static ErpGrant RoleIndirectRecordGrant(string action) => new()
    {
        Action = action,
        Entity = "warehouse_transfer",
        Scope = PermissionGrantScopes.Record,
        RecordScope = new ErpRecordScope
        {
            Field = "destination_warehouse",
            Source = RecordPermissionScopeSources.LinkedRecord,
            LinkedEntity = "warehouse_user",
            LinkedField = "warehouse"
        }
    };

    private static ErpGrant RoleActorPrincipalRecordGrant(string action) => new()
    {
        Action = action,
        Entity = "warehouse_transfer",
        Scope = PermissionGrantScopes.Record,
        RecordScope = new ErpRecordScope
        {
            Field = "owner_principal",
            Source = RecordPermissionScopeSources.ActorPrincipal
        }
    };

    private static List<ErpGrant> ExactFlatApprovalGrants() =>
    [
        new ErpGrant { Action = "read", Entity = "request" },
        new ErpGrant { Action = "read_task", Entity = "request" },
        new ErpGrant { Action = "approve_task", Entity = "request" }
    ];

    private static ErpGrant FieldReadGrant(string field) => new()
    {
        Action = "read",
        Entity = "request",
        Scope = PermissionGrantScopes.Field,
        Target = field
    };

    private static List<ErpGrant> DirectRecordApprovalGrants(string field, string linkedEntity) =>
    [
        DirectRecordGrant("read", field, linkedEntity),
        new ErpGrant { Action = "read_task", Entity = "request" },
        DirectRecordGrant("approve_task", field, linkedEntity)
    ];

    private static ErpGrant DirectRecordGrant(string action, string field, string linkedEntity) => new()
    {
        Action = action,
        Entity = "request",
        Scope = PermissionGrantScopes.Record,
        RecordScope = new ErpRecordScope
        {
            Field = field,
            Source = RecordPermissionScopeSources.LinkedRecord,
            LinkedEntity = linkedEntity
        }
    };

    private static ErpField TextField(string id, string displayName) => new()
    {
        Id = id,
        Name = id,
        DisplayName = displayName,
        Type = "string",
        Required = true
    };

    private static ErpField RelationField(string id, string displayName, string references) => new()
    {
        Id = id,
        Name = $"{id}_id",
        DisplayName = displayName,
        Type = "relation",
        Required = true,
        References = references
    };

    private static ErpBundle BundleWithCondition(ErpWorkflowCondition condition, List<ErpField>? fields = null)
    {
        var bundle = MinimalBundle(fields ??
        [
            new ErpField
            {
                Id = "name",
                Name = "name",
                DisplayName = "Name",
                Type = "string",
                Required = true
            },
            new ErpField
            {
                Id = "hours",
                Name = "hours",
                DisplayName = "Hours",
                Type = "number",
                Required = true
            }
        ]);
        bundle.Roles["owner"].Grants.AddRange(
        [
            new ErpGrant { Action = "read", Entity = "customer" },
            new ErpGrant { Action = "read_task", Entity = "customer" },
            new ErpGrant { Action = "approve_task", Entity = "customer" }
        ]);
        bundle.Modules[0].Workflows.Add(new ErpWorkflow
        {
            Id = "conditional_workflow",
            Name = "Conditional Workflow",
            Entity = "customer",
            Trigger = new ErpWorkflowTrigger { Action = "submit" },
            Steps =
            [
                new ErpWorkflowStep
                {
                    Id = "condition_step",
                    Type = "condition",
                    Condition = condition,
                    Then =
                    [
                        new ErpWorkflowStep
                        {
                            Id = "owner_approval",
                            Type = "approval",
                            AssignedTo = new ErpWorkflowAssignment
                            {
                                Type = "role",
                                Role = "owner"
                            }
                        }
                    ]
                }
            ]
        });

        return bundle;
    }

    private static ErpBundle ActionBundle()
    {
        var bundle = new ErpBundle
        {
            Id = "action_bundle",
            Name = "Action Bundle",
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
                            Id = "customer",
                            Name = "customers",
                            DisplayName = "Customers",
                            Fields =
                            [
                                new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
                            ]
                        },
                        new ErpEntity
                        {
                            Id = "invoice",
                            Name = "invoices",
                            DisplayName = "Invoices",
                            Fields =
                            [
                                new ErpField { Id = "customer", Name = "customer_id", DisplayName = "Customer", Type = "relation", Required = true, References = "customer" },
                                new ErpField { Id = "amount", Name = "amount", DisplayName = "Amount", Type = "money", Required = true }
                            ]
                        }
                    ]
                }
            ],
            Actions = [ActionDefinitionForTest()],
            Roles = new Dictionary<string, ErpRole>
            {
                ["owner"] = new()
                {
                    DisplayName = "Owner",
                    Grants = [new ErpGrant { Entity = "*", Action = "*" }]
                }
            }
        };

        return bundle;
    }

    private static string HashWithActionAmount(object value)
    {
        var bundle = ActionBundle();
        bundle.Actions[0].Steps[1].Values["amount"] = new ErpActionValue { Value = value };
        return BundleHashCalculator.ComputeHash(bundle);
    }

    private static string HashLoadedActionAmount(string yamlScalar)
    {
        var bundle = new ErpBundleYamlLoader().Load(ActionAmountYaml(
            $"""
            amount:
              value: {yamlScalar}
            """));
        return BundleHashCalculator.ComputeHash(bundle);
    }

    private static string ActionAmountYaml(string values) =>
        $$"""
        id: yaml_numeric_bundle
        name: YAML Numeric Bundle
        version: 1.0.0
        modules: []
        actions:
          - key: numeric_action
            label: Numeric Action
            entity: sample
            type: record_action
            steps:
              - id: create_sample
                type: create_record
                entity: sample
                values:
        {{string.Join('\n', values.Split('\n').Select(line => $"          {line}"))}}
        """;

    private static JsonElement JsonNumber(string rawNumber)
    {
        using var document = JsonDocument.Parse(rawNumber);
        return document.RootElement.Clone();
    }

    private static ErpAction ActionDefinitionForTest(
        string entity = "customer",
        string label = "Create Invoice",
        List<ErpActionStep>? steps = null,
        ErpActionRequires? requires = null) => new()
    {
        Key = "create_invoice",
        Label = label,
        Entity = entity,
        Type = "record_action",
        Requires = requires ?? new ErpActionRequires
        {
            Permissions =
            [
                new ErpGrant { Entity = "customer", Action = "read" },
                new ErpGrant { Entity = "invoice", Action = "create" }
            ]
        },
        Steps = steps ?? ActionStepsForTest()
    };

    private static List<ErpActionStep> ActionStepsForTest() =>
    [
        QueryCustomersStep("id"),
        CreateInvoiceStep(new Dictionary<string, ErpActionValue>
        {
            ["customer"] = new() { ValueFrom = new ErpActionValueFrom { Record = "id" } },
            ["amount"] = new() { Value = 10 }
        })
    ];

    private static ErpActionStep QueryCustomersStep(string field) => new()
    {
        Id = "find_customers",
        Type = "query_records",
        Entity = "customer",
        Where =
        [
            new ErpActionFilter
            {
                Field = field,
                Op = "eq",
                ValueFrom = new ErpActionValueFrom { Record = "id" }
            }
        ]
    };

    private static ErpActionStep CreateInvoiceStep(Dictionary<string, ErpActionValue> values, string id = "create_invoice") => new()
    {
        Id = id,
        Type = "create_record",
        Entity = "invoice",
        Values = values
    };
}
