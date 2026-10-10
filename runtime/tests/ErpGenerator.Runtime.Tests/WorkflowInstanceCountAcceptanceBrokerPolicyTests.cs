using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Services.DeploymentBroker;

namespace ErpGenerator.Runtime.Tests;

public sealed class WorkflowInstanceCountAcceptanceBrokerPolicyTests
{
    [Fact]
    public void Broker_accepts_exact_v3_workflow_instance_count()
    {
        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests([Contract()]);

        validate.Should().NotThrow();
    }

    [Fact]
    public void Broker_rejects_workflow_instance_count_under_v2()
    {
        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [Contract(schema: BundleAcceptanceTestSchemas.V2)]);

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*workflow_instance_count assertions require exact v3*");
    }

    [Theory]
    [InlineData("missing_workflow")]
    [InlineData("missing_entity")]
    [InlineData("missing_record")]
    [InlineData("negative_count")]
    [InlineData("null_count")]
    [InlineData("workflow_ref")]
    [InlineData("non_start_checkpoint")]
    public void Broker_rejects_malformed_workflow_instance_count(string variant)
    {
        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [Contract(variant)]);

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*workflow_instance_count assertions require exact v3*");
    }

    [Fact]
    public void Compiled_broker_accepts_one_exact_workflow_for_the_produced_record()
    {
        var document = Contract();
        BundleAcceptanceTestContractHasher.Stamp([document]);

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            Compiled());

        validate.Should().NotThrow();
    }

    [Theory]
    [InlineData("missing_workflow")]
    [InlineData("account_approval")]
    public void Compiled_broker_rejects_unknown_or_wrong_entity_workflow(string workflow)
    {
        var document = Contract();
        document.Assertions[0] = CopyAssertion(document.Assertions[0], workflow: workflow);
        BundleAcceptanceTestContractHasher.Stamp([document]);

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            Compiled());

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*exact compiled workflow*");
    }

    [Fact]
    public void Compiled_broker_rejects_an_unrelated_existing_workflow_at_the_start_checkpoint()
    {
        var document = ContractWithStartCheckpoint();
        BundleAcceptanceTestContractHasher.Stamp([document]);

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            Compiled());

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*exact compiled workflow, entity, record ref, and trigger action*");
    }

    private static BundleAcceptanceTestDocument Contract(
        string variant = "valid",
        string schema = BundleAcceptanceTestSchemas.V3) => new()
    {
        Schema = schema,
        Test = new BundleAcceptanceTest
        {
            Key = "workflow_instance_count_broker",
            Label = "Workflow instance count broker"
        },
        Setup = new BundleAcceptanceSetup
        {
            Seed = "none",
            SeedHash = new string('a', 64)
        },
        Actors = [new BundleAcceptanceActor { Id = "operator", Roles = ["operator"] }],
        Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "create_job",
                Type = "create_record",
                Entity = "job",
                AsActor = "operator",
                AsRole = "operator",
                Values = new Dictionary<string, object?> { ["name"] = "Broker proof" }
            },
            new BundleAcceptanceStep
            {
                Id = "start_job",
                Type = "start_workflow_action",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                Action = "start_job_approval",
                AsActor = "operator",
                AsRole = "operator"
            }
        ],
        Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Id = "job_workflow_instance_count",
                Type = "workflow_instance_count",
                AfterStep = variant == "non_start_checkpoint" ? "create_job" : "start_job",
                Entity = variant == "missing_entity" ? string.Empty : "job",
                Record = new BundleRecordSelector
                {
                    Ref = variant == "missing_record" ? string.Empty : "create_job"
                },
                Workflow = variant == "missing_workflow" ? string.Empty : "job_approval",
                WorkflowRef = variant == "workflow_ref" ? "start_job" : string.Empty,
                Count = variant switch
                {
                    "negative_count" => -1,
                    "null_count" => null,
                    _ => 0
                }
            }
        ]
    };

    private static BundleAcceptanceTestDocument ContractWithStartCheckpoint()
    {
        var document = Contract();
        document.Assertions[0] = CopyAssertion(
            document.Assertions[0],
            workflow: "unrelated_job_workflow");
        return document;
    }

    private static BundleAcceptanceAssertion CopyAssertion(
        BundleAcceptanceAssertion source,
        string? workflow = null,
        string? afterStep = null) => new()
    {
        Id = source.Id,
        Type = source.Type,
        AfterStep = afterStep ?? source.AfterStep,
        Entity = source.Entity,
        Record = new BundleRecordSelector { Ref = source.Record.Ref },
        Workflow = workflow ?? source.Workflow,
        WorkflowRef = source.WorkflowRef,
        Count = source.Count
    };

    private static CompiledBundle Compiled() => new(
        "workflow_count",
        "Workflow count",
        "1.0.0",
        new string('b', 64),
        [Entity("job"), Entity("account")],
        [],
        [
            Workflow("job_approval", "job", "start_job_approval"),
            Workflow("unrelated_job_workflow", "job", "start_other_job_workflow"),
            Workflow("account_approval", "account", "start_account_approval")
        ],
        [],
        [new RoleDefinition("operator", "Operator", [])],
        [],
        new DashboardDefinition([]),
        [],
        []);

    private static EntityDefinition Entity(string id) => new(
        id,
        "operations",
        id,
        id,
        id,
        new EntityDisplayDefinition("name", null),
        []);

    private static WorkflowDefinition Workflow(string id, string entity, string trigger) => new(
        id,
        "operations",
        id,
        entity,
        [],
        new WorkflowTriggerDefinition(trigger),
        [],
        []);
}
