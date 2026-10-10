using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Services.DeploymentBroker;

namespace ErpGenerator.Runtime.Tests;

public sealed class CanonicalBundleDeploymentTests
{
    [Fact]
    public void Canonical_generated_bundle_maps_to_a_valid_runtime_bundle()
    {
        var bundle = Bundle();
        var request = new CanonicalBundleDeploymentRequest(
            "operation-1",
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            new RuntimeDeploymentTarget("tenant-1", "stable-1", "acme.example.test", "erp", "erp_tenant_stable1_staging"),
            CanonicalBundleDeploymentService.ComputeArtifactHash(bundle),
            bundle,
            "owner@example.test");

        var mapped = CanonicalBundleDeploymentService.Map(request);
        var compiled = new ErpBundleCompiler().Compile(mapped);

        Assert.Equal(2, compiled.Entities.Count);
        Assert.Single(compiled.Workflows);
        Assert.Single(compiled.Reports);
        Assert.Single(compiled.Dashboard.Widgets);
        Assert.Equal("customer_id", compiled.Entities.Single(value => value.Id == "sales_order").Display.SecondaryField);

        var manager = mapped.Roles["sales_manager"];
        Assert.Contains(manager.Grants, value => value.Action == "read" && value.Entity == "sales_order");
        Assert.Contains(manager.Grants, value =>
            value.Scope == "page" && value.Target == "report_detail" && value.Entity == "sales_orders");
        Assert.Contains(manager.Grants, value =>
            value.Scope == "page" && value.Target == "dashboard" && value.Entity == "*");

        var acceptanceTests = CanonicalBundleDeploymentService.BuildAcceptanceTests(request.Bundle);
        Assert.Single(acceptanceTests);
        Assert.All(acceptanceTests, value =>
        {
            Assert.Equal(BundleAcceptanceTestSchemas.V2, value.Schema);
            Assert.Equal(64, value.Test.ContractHash.Length);
            Assert.Equal(64, value.Test.SuiteHash.Length);
        });
    }

    [Fact]
    public void Canonical_acceptance_record_refs_are_unique_across_tests()
    {
        var bundle = Bundle() with
        {
            AcceptanceTests =
            [
                new CanonicalGeneratedAcceptanceTest(
                    "create_customer_one", "First customer", "entity_create", "customer", null,
                    [new CanonicalGeneratedAcceptanceValue("name", "First customer")]),
                new CanonicalGeneratedAcceptanceTest(
                    "create_customer_two", "Second customer", "entity_create", "customer", null,
                    [new CanonicalGeneratedAcceptanceValue("name", "Second customer")])
            ]
        };

        var tests = CanonicalBundleDeploymentService.BuildAcceptanceTests(bundle);
        var producerRefs = tests.Select(test => Assert.Single(test.Steps).Id).ToArray();

        Assert.Equal(2, producerRefs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tests, test =>
            Assert.Equal(Assert.Single(test.Steps).Id, Assert.Single(test.Assertions).Record!.Ref));
    }

    [Fact]
    public void Canonical_deployment_rejects_an_artifact_hash_from_different_content()
    {
        var bundle = Bundle();
        var request = new CanonicalBundleDeploymentRequest(
            "operation-1",
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            new RuntimeDeploymentTarget("tenant-1", "stable-1", "acme.example.test", "erp", "erp_tenant_stable1_staging"),
            new string('a', 64),
            bundle,
            "owner@example.test");

        var validate = () => CanonicalBundleDeploymentService.ValidateRequest(request);

        Assert.Throws<ArgumentException>(validate);
    }

    private static CanonicalGeneratedErpBundle Bundle() => new(
        "Acme ERP",
        "Sales order control",
        ["Sales"],
        [
            new CanonicalGeneratedEntity("sales_order", "Sales orders",
            [
                new CanonicalGeneratedField("order_number", "Order number", "text", true, null),
                new CanonicalGeneratedField("customer", "Customer", "reference", true, "customer"),
                new CanonicalGeneratedField("status", "Status", "text", true, null)
            ]),
            new CanonicalGeneratedEntity("customer", "Customers",
            [
                new CanonicalGeneratedField("customer_number", "Customer number", "text", true, null),
                new CanonicalGeneratedField("name", "Name", "text", true, null)
            ])
        ],
        [
            new CanonicalGeneratedWorkflow(
                "approve_sales_order",
                "Approve sales order",
                "sales_order",
                "submit_for_approval",
                "sales_manager",
                "status",
                "approved",
                ["Manager approval"])
        ],
        [new CanonicalGeneratedReport("sales_orders", "Sales orders", "sales_order", ["status"])],
        [new CanonicalGeneratedDashboard("sales", "Sales", ["sales_orders"])],
        [new CanonicalGeneratedPermission(
            "sales_manager",
            ["start_workflow:sales_order", "read:sales_orders", "read:sales"])],
        [new CanonicalGeneratedAcceptanceTest(
            "create_sales_order", "Create sales order", "entity_create", "sales_order", null,
            [new CanonicalGeneratedAcceptanceValue("order_number", "SO-TEST"), new CanonicalGeneratedAcceptanceValue("status", "draft")])],
        [],
        []);
}
