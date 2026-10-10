using FluentAssertions;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class ManufacturingBundleTests
{
    [Fact]
    public async Task Manufacturing_v1_folder_bundle_loads_and_compiles()
    {
        var compiled = await RuntimeBundleTestHelper.CompileManufacturingV1FolderAsync();

        compiled.BundleId.Should().Be("manufacturing_demo");
        compiled.BundleName.Should().Be("Manufacturing Demo ERP");
        compiled.Version.Should().Be("0.1.0");
        compiled.Entities.Should().HaveCount(23);
        compiled.Views.Should().HaveCount(10);
        compiled.Workflows.Should().HaveCount(4);
        compiled.Actions.Should().HaveCount(4);
        compiled.Reports.Should().HaveCount(9);
        compiled.Dashboard.Widgets.Should().HaveCount(7);
        compiled.Roles.Select(role => role.Id).Should().BeEquivalentTo(
            "owner",
            "admin",
            "sales",
            "purchasing",
            "production_manager",
            "operator",
            "quality",
            "readonly");
    }

    [Fact]
    public async Task Manufacturing_v1_folder_bundle_contains_expected_runtime_mapping()
    {
        var compiled = await RuntimeBundleTestHelper.CompileManufacturingV1FolderAsync();

        compiled.Entities.Select(entity => entity.Id).Should().Contain(
            "customer",
            "vendor",
            "item",
            "product",
            "bill_of_material",
            "bill_of_material_line",
            "sales_order",
            "sales_order_line",
            "purchase_order",
            "purchase_order_line",
            "work_order",
            "material_issue",
            "production_receipt",
            "quality_inspection");
        compiled.Workflows.Select(workflow => workflow.Id).Should().BeEquivalentTo(
            "sales_order_approval",
            "purchase_order_approval",
            "work_order_release",
            "quality_inspection");
        compiled.Actions.Select(action => action.Key).Should().BeEquivalentTo(
            "create_work_order_from_sales_order",
            "issue_materials_to_work_order",
            "complete_work_order",
            "receive_purchase_order");
        compiled.Reports.Select(report => report.Key).Should().Contain(
            "sales_orders_by_status",
            "open_work_orders",
            "work_orders_by_status",
            "stock_on_hand",
            "inventory_movements",
            "purchase_orders_by_status",
            "quality_inspections_by_result",
            "production_receipts",
            "items_at_or_below_reorder_point");

        var salesOrderLine = compiled.Entities.Single(entity => entity.Id == "sales_order_line");
        salesOrderLine.Fields.Single(field => field.Id == "line_total").Computed.Should().NotBeNull();

        var stockBalance = compiled.Entities.Single(entity => entity.Id == "stock_balance");
        stockBalance.Fields.Single(field => field.Id == "item").Required.Should().BeFalse();
        stockBalance.Fields.Single(field => field.Id == "product").Required.Should().BeFalse();
    }

    [Fact]
    public async Task Manufacturing_v1_seed_and_acceptance_tests_load()
    {
        var folder = RuntimeBundleTestHelper.FindManufacturingV1Folder();
        var loader = new ErpBundleYamlLoader();

        var seed = await loader.LoadSeedFromDirectoryAsync(folder, "demo");
        var tests = await loader.LoadAcceptanceTestsFromDirectoryAsync(folder);

        seed.Should().NotBeNull();
        seed!.Records.Should().HaveCount(31);
        tests.Select(test => test.Test.Key).Should().BeEquivalentTo(
            "sales_order_to_work_order",
            "work_order_production_flow",
            "purchase_order_receipt",
            "quality_inspection",
            "reports",
            "workflow_business_role_approvals");
    }

    [PostgresFact]
    public async Task Manufacturing_v1_seed_and_acceptance_tests_pass_when_postgres_is_available()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_mfg_stg_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create("manufacturing", "manufacturing.erpgenerator.example", database, TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var actions = new RuntimeActionStore(factory, new RuntimePermissionService(factory));
            var reports = new RuntimeReportStore(factory);
            var seedRunner = new RuntimeSeedRunner(records);
            var testRunner = new RuntimeBundleTestRunner(factory, seedRunner, records, workflows, actions, reports);
            var loader = new ErpBundleYamlLoader();
            var compiled = await RuntimeBundleTestHelper.CompileManufacturingV1FolderAsync();
            var plan = new ErpBundleMigrationPlanner().PlanMigration(null, compiled);

            await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                plan,
                "staging",
                "staged",
                activate: true,
                "manufacturing v1 staged");

            var seed = await loader.LoadSeedFromDirectoryAsync(RuntimeBundleTestHelper.FindManufacturingV1Folder(), "demo");
            var acceptanceTests = await loader.LoadAcceptanceTestsFromDirectoryAsync(RuntimeBundleTestHelper.FindManufacturingV1Folder());

            var result = await testRunner.RunAsync(tenant, compiled.Version, seed, acceptanceTests);
            var diagnostics = string.Join(" | ", result.Results
                .Where(test => test.Status != "passed")
                .Select(test => $"{test.Key}: {string.Join("; ", test.Failures)}"));

            result.Status.Should().Be("passed", diagnostics);
            result.Passed.Should().Be(6);
            result.Failed.Should().Be(0);
            (await testRunner.HasPassingResultAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                result.TestSuiteHash)).Should().BeTrue();
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    [PostgresFact]
    public async Task Manufacturing_v1_runtime_user_links_use_generated_employee_display_fields()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_mfg_users_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create("manufacturing", "manufacturing.erpgenerator.example", database, TenantEnvironmentKind.Production);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var users = new RuntimeUserStore(factory);
            var compiled = await RuntimeBundleTestHelper.CompileManufacturingV1FolderAsync();
            var plan = new ErpBundleMigrationPlanner().PlanMigration(null, compiled);

            await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                plan,
                "production",
                "production",
                activate: true,
                "manufacturing v1 promoted");

            var owner = await RuntimeUserTestHelper.ActivateOwnerAsync(
                runtimeDatabase,
                tenant,
                $"owner-{suffix}@factory.example",
                name: "Manufacturing Owner");
            var employee = await records.CreateRecordAsync(tenant, owner, "employees", RuntimeJson.JsonObject(
                ("name", $"Manufacturing Employee {suffix}"),
                ("email", $"employee-{suffix}@factory.example"),
                ("role", "operator"),
                ("active", true)));

            (await users.LinkEmployeeAsync(tenant, owner, owner.UserId, Convert.ToInt64(employee["id"]))).Should().BeTrue();

            var runtimeUsers = await users.ListUsersAsync(tenant);

            runtimeUsers.Should().ContainSingle(user =>
                user.Email == owner.Email &&
                user.LinkedEmployee != null &&
                user.LinkedEmployee.Label == $"Manufacturing Employee {suffix} (operator)");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }
}
