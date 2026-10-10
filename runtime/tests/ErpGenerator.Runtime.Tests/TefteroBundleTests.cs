using FluentAssertions;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class TefteroBundleTests
{
    [Fact]
    public async Task Teftero_v1_folder_bundle_loads_and_compiles()
    {
        var compiled = await CompileTefteroV1FolderAsync();

        compiled.BundleId.Should().Be("teftero_erp");
        compiled.BundleName.Should().Be("Teftero ERP");
        compiled.Version.Should().Be("0.1.0");
        compiled.Entities.Should().HaveCount(31);
        compiled.Views.Should().HaveCount(8);
        compiled.Workflows.Should().HaveCount(2);
        compiled.Actions.Should().HaveCount(2);
        compiled.Reports.Should().HaveCount(9);
        compiled.Dashboard.Widgets.Should().HaveCount(7);
        compiled.Roles.Select(role => role.Id).Should().BeEquivalentTo(
            "owner",
            "admin",
            "sales",
            "delivery",
            "finance",
            "readonly");
    }

    [Fact]
    public async Task Teftero_v1_folder_bundle_contains_expected_runtime_mapping()
    {
        var compiled = await CompileTefteroV1FolderAsync();

        compiled.Entities.Select(entity => entity.Id).Should().Contain(
            "company",
            "contact",
            "employee",
            "prospect",
            "offer",
            "offer_price_line",
            "contract",
            "contract_price_line",
            "time_entry",
            "task",
            "subtask_time_log");
        compiled.Actions.Select(action => action.Key).Should().BeEquivalentTo(
            "create_offer_from_prospect",
            "generate_contract_from_offer");
        compiled.Actions.Single(action => action.Key == "create_offer_from_prospect")
            .Steps.Select(step => step.Type)
            .Should().Contain("update_current_record");
        compiled.Actions.Single(action => action.Key == "generate_contract_from_offer")
            .Steps.Select(step => step.Type)
            .Should().Contain("update_current_record");
        compiled.Workflows.Select(workflow => workflow.Id).Should().BeEquivalentTo(
            "time_entry_approval",
            "task_status");
        compiled.Reports.Select(report => report.Key).Should().Contain(
            "prospects_by_status",
            "offers_by_status",
            "active_contracts",
            "billable_hours_by_contract",
            "tasks_by_status");

        var prospect = compiled.Entities.Single(entity => entity.Id == "prospect");
        prospect.Fields.Select(field => field.Id).Should().Contain(["converted", "generated_offer"]);
        prospect.Fields.Single(field => field.Id == "generated_offer").References.Should().Be("offer");

        var offer = compiled.Entities.Single(entity => entity.Id == "offer");
        offer.Fields.Select(field => field.Id).Should().Contain(["is_generated_contract", "generated_contract"]);
        offer.Fields.Single(field => field.Id == "generated_contract").References.Should().Be("contract");
    }

    [Fact]
    public async Task Teftero_v1_seed_and_acceptance_tests_load()
    {
        var folder = FindTefteroV1Folder();
        var loader = new ErpBundleYamlLoader();

        var seed = await loader.LoadSeedFromDirectoryAsync(folder, "demo");
        var tests = await loader.LoadAcceptanceTestsFromDirectoryAsync(folder);

        seed.Should().NotBeNull();
        seed!.Records.Should().HaveCount(36);
        tests.Select(test => test.Test.Key).Should().BeEquivalentTo(
            "prospect_to_offer",
            "offer_to_contract",
            "time_entry_approval",
            "task_status",
            "reports");
    }

    [PostgresFact]
    public async Task Teftero_v1_seed_and_acceptance_tests_pass_when_postgres_is_available()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_teftero_stg_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create("teftero", "teftero.erpgenerator.example", database, TenantEnvironmentKind.Erp);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var runtimeDatabase = new RuntimeDatabase(factory, new RuntimeSqlBuilder());
            var records = new RuntimeRecordStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var actions = new RuntimeActionStore(factory, new RuntimePermissionService(factory));
            var reports = new RuntimeReportStore(factory);
            var seedRunner = new RuntimeSeedRunner(records);
            var testRunner = new RuntimeBundleTestRunner(factory, seedRunner, records, workflows, actions, reports);
            var loader = new ErpBundleYamlLoader();
            var compiled = await CompileTefteroV1FolderAsync();
            var plan = new ErpBundleMigrationPlanner().PlanMigration(null, compiled);

            await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                plan,
                "erp",
                "staged",
                activate: true,
                "teftero v1 staged");

            var seed = await loader.LoadSeedFromDirectoryAsync(FindTefteroV1Folder(), "demo");
            var acceptanceTests = await loader.LoadAcceptanceTestsFromDirectoryAsync(FindTefteroV1Folder());

            var result = await testRunner.RunAsync(tenant, compiled.Version, seed, acceptanceTests);
            var diagnostics = string.Join(" | ", result.Results
                .Where(test => test.Status != "passed")
                .Select(test => $"{test.Key}: {string.Join("; ", test.Failures)}"));

            result.Status.Should().Be("passed", diagnostics);
            result.Passed.Should().Be(5);
            result.Failed.Should().Be(0);
            (await testRunner.HasPassingResultAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                result.TestSuiteHash)).Should().BeTrue();

            var metadata = await runtimeDatabase.ReadMetadataAsync(tenant);
            metadata.BundleVersion.Should().Be("0.1.0");
            metadata.Actions.Should().Contain(action => action.GetProperty("Key").GetString() == "generate_contract_from_offer");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static async Task<CompiledBundle> CompileTefteroV1FolderAsync() =>
        new ErpBundleCompiler().Compile(await new ErpBundleYamlLoader().LoadDirectoryAsync(FindTefteroV1Folder()));

    private static string FindTefteroV1Folder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "bundles", "teftero", "v1");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("bundles/teftero/v1");
    }
}
