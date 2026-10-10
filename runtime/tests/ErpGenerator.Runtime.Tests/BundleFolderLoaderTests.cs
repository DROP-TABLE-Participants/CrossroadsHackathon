using System.Text.Json;
using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class BundleFolderLoaderTests
{
    [Fact]
    public async Task Folder_bundle_loads_and_compiles_to_same_essential_v4_shape_as_single_file()
    {
        var loader = new ErpBundleYamlLoader();
        var compiler = new ErpBundleCompiler();

        var folder = compiler.Compile(await loader.LoadDirectoryAsync(RuntimeBundleTestHelper.FindItServicesV4Folder()));
        var file = compiler.Compile(await loader.LoadFileAsync(RuntimeBundleTestHelper.FindBundlePath("sample-erp-v4.yaml")));

        folder.BundleId.Should().Be(file.BundleId);
        folder.Version.Should().Be("0.4.0");
        folder.Entities.Select(entity => entity.Id).Should().BeEquivalentTo(file.Entities.Select(entity => entity.Id));
        folder.Views.Select(view => view.Id).Should().BeEquivalentTo(file.Views.Select(view => view.Id));
        folder.Workflows.Select(workflow => workflow.Id).Should().BeEquivalentTo(file.Workflows.Select(workflow => workflow.Id));
        folder.Actions.Select(action => action.Key).Should().BeEquivalentTo(file.Actions.Select(action => action.Key));
        folder.Roles.Should().BeEquivalentTo(file.Roles);
        folder.Roles
            .Where(role => role.Grants.Any(grant =>
                grant.Scope == PermissionGrantScopes.Action &&
                grant.Action == GranularPermissionActions.Execute &&
                grant.Entity == "project" &&
                grant.Target == "create_invoice_draft"))
            .Select(role => role.Id)
            .Should().BeEquivalentTo("owner", "admin", "finance");
        folder.Roles.Single(role => role.Id == "finance").Grants.Should().Contain(grant =>
            grant.Scope == PermissionGrantScopes.Entity &&
            grant.Action == "update" &&
            grant.Entity == "timesheet");
        folder.Reports.Select(report => report.Key).Should().BeEquivalentTo(file.Reports.Select(report => report.Key));
        folder.Dashboard.Widgets.Select(widget => widget.Key).Should().BeEquivalentTo(file.Dashboard.Widgets.Select(widget => widget.Key));
    }

    [Fact]
    public async Task Folder_v4_preserves_v3_permission_authority()
    {
        var loader = new ErpBundleYamlLoader();
        var compiler = new ErpBundleCompiler();
        var v3 = compiler.Compile(await loader.LoadFileAsync(RuntimeBundleTestHelper.FindBundlePath("sample-erp-v3.yaml")));
        var folderV4 = compiler.Compile(await loader.LoadDirectoryAsync(RuntimeBundleTestHelper.FindItServicesV4Folder()));

        var plan = new ErpBundleMigrationPlanner().PlanMigration(v3, folderV4);

        plan.IsBlocked.Should().BeFalse();
        plan.Operations.Should().NotContain(operation => operation.Type == "blocked_remove_permission");
    }

    [Fact]
    public async Task Path_loader_keeps_single_file_bundle_compatibility()
    {
        var loader = new ErpBundleYamlLoader();
        var bundle = await loader.LoadPathAsync(RuntimeBundleTestHelper.FindBundlePath("sample-erp-v4.yaml"));

        bundle.Version.Should().Be("0.4.0");
        bundle.Actions.Should().ContainSingle(action => action.Key == "create_invoice_draft");
    }

    [Fact]
    public async Task Folder_bundle_identity_is_invariant_to_definition_filenames()
    {
        var directory = RuntimeBundleTestHelper.CopyItServicesV4FolderToTemp();
        try
        {
            var loader = new ErpBundleYamlLoader();
            var compiler = new ErpBundleCompiler();
            var baseline = await loader.LoadDirectoryAsync(directory);
            var baselineHash = compiler.Compile(baseline).BundleHash;

            File.Move(
                Path.Combine(directory, "entities", "customer.yaml"),
                Path.Combine(directory, "entities", "zz--customer.yaml"));
            File.Move(
                Path.Combine(directory, "views", "active-projects.yaml"),
                Path.Combine(directory, "views", "zz--active-projects.yaml"));
            File.Move(
                Path.Combine(directory, "workflows", "expense-approval.yaml"),
                Path.Combine(directory, "workflows", "zz--expense-approval.yaml"));
            File.Move(
                Path.Combine(directory, "actions", "create-invoice-draft.yaml"),
                Path.Combine(directory, "actions", "zz--create-invoice-draft.yaml"));
            File.Move(
                Path.Combine(directory, "reports", "approved-expenses.yaml"),
                Path.Combine(directory, "reports", "zz--approved-expenses.yaml"));
            File.Move(
                Path.Combine(directory, "permissions", "roles.yaml"),
                Path.Combine(directory, "permissions", "zz--roles.yaml"));

            var renamed = await loader.LoadDirectoryAsync(directory);

            JsonSerializer.Serialize(renamed).Should().Be(
                JsonSerializer.Serialize(baseline),
                "folder filenames are transport details, not bundle identity");
            compiler.Compile(renamed).BundleHash.Should().Be(baselineHash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Folder_bundle_requires_manifest()
    {
        var directory = RuntimeBundleTestHelper.CreateTempDirectory();
        var loader = new ErpBundleYamlLoader();

        var act = () => loader.LoadDirectoryAsync(directory);

        var ex = await act.Should().ThrowAsync<BundleCompilationException>();
        ex.Which.Errors.Should().Contain(error => error.Contains("manifest.yaml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Folder_bundle_rejects_duplicate_entity_with_source_paths()
    {
        var directory = RuntimeBundleTestHelper.CopyItServicesV4FolderToTemp();
        File.Copy(
            Path.Combine(directory, "entities", "customer.yaml"),
            Path.Combine(directory, "entities", "customer-copy.yaml"));
        var loader = new ErpBundleYamlLoader();

        var act = () => loader.LoadDirectoryAsync(directory);

        var ex = await act.Should().ThrowAsync<BundleCompilationException>();
        ex.Which.Errors.Should().Contain(error =>
            error.Contains("customer-copy.yaml", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("customer.yaml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Folder_bundle_reference_error_includes_source_filename()
    {
        var directory = RuntimeBundleTestHelper.CopyItServicesV4FolderToTemp();
        var projectPath = Path.Combine(directory, "entities", "project.yaml");
        var yaml = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(projectPath, yaml.Replace("references: employee", "references: Employeee", StringComparison.Ordinal));
        var loader = new ErpBundleYamlLoader();

        var act = () => loader.LoadDirectoryAsync(directory);

        var ex = await act.Should().ThrowAsync<BundleCompilationException>();
        ex.Which.Errors.Should().Contain(error =>
            error.Contains("entities/project.yaml", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("Employeee", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Folder_entity_rejects_misspelled_lifecycle_policy_with_source_filename()
    {
        var directory = RuntimeBundleTestHelper.CopyItServicesV4FolderToTemp();
        var customerPath = Path.Combine(directory, "entities", "customer.yaml");
        var yaml = await File.ReadAllTextAsync(customerPath);
        await File.WriteAllTextAsync(
            customerPath,
            yaml.Replace(
                "    unique: true",
                "    unique: true\n    lifecyclePolicy:\n      initial: Draft\n      transitions: direct",
                StringComparison.Ordinal));

        var load = () => new ErpBundleYamlLoader().LoadDirectoryAsync(directory);

        var exception = await load.Should().ThrowAsync<BundleCompilationException>();
        exception.Which.Errors.Should().Contain(error =>
            error.Contains("entities/customer.yaml", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("lifecycle-like", StringComparison.OrdinalIgnoreCase));
    }

}
