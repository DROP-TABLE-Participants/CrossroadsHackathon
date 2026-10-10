using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Services.DeploymentBroker;

namespace ErpGenerator.Runtime.Tests;

public sealed class ActionProducedRecordBrokerPolicyTests
{
    [Fact]
    public void Compiled_broker_rejects_an_ordinary_value_ref_to_an_action_with_mismatched_return()
    {
        var document = Contract();
        BundleAcceptanceTestContractHasher.Stamp([document]);

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            Compiled());

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*without one exact explicitly returned created record*");
    }

    private static BundleAcceptanceTestDocument Contract() => new()
    {
        Schema = BundleAcceptanceTestSchemas.V3,
        Test = new BundleAcceptanceTest { Key = "broker_action_value_ref", Label = "Broker action value ref" },
        Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = new string('a', 64) },
        Actors = [new BundleAcceptanceActor { Id = "operator", Roles = ["operator"] }],
        Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "create_source",
                Type = "create_record",
                Entity = "source",
                AsActor = "operator",
                AsRole = "operator",
                Values = new Dictionary<string, object?> { ["name"] = "Source" }
            },
            new BundleAcceptanceStep
            {
                Id = "create_target_action",
                Type = "run_action",
                Entity = "source",
                Record = new BundleRecordSelector { Ref = "create_source" },
                Action = "create_target",
                AsActor = "operator",
                AsRole = "operator"
            },
            new BundleAcceptanceStep
            {
                Id = "create_consumer",
                Type = "create_record",
                Entity = "consumer",
                AsActor = "operator",
                AsRole = "operator",
                Values = new Dictionary<string, object?>
                {
                    ["name"] = "Consumer",
                    ["target"] = 1L
                }
            }
        ],
        Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Id = "consumer_target_matches",
                AfterStep = "create_consumer",
                Type = "record_field_equals",
                Entity = "consumer",
                Record = new BundleRecordSelector { Ref = "create_consumer" },
                Field = "target",
                ValueRef = "create_target_action"
            }
        ]
    };

    private static CompiledBundle Compiled() => new(
        "broker_action_value_ref",
        "Broker action value ref",
        "1.0.0",
        new string('b', 64),
        [Entity("source"), Entity("target"), ConsumerEntity()],
        [],
        [],
        [
            new ActionDefinition(
                "create_target",
                "Create target",
                "source",
                "record",
                null,
                [],
                [],
                [
                    Step("create_target_record", "create_record", "target"),
                    Step(
                        "return_target",
                        "return_result",
                        "target",
                        new ActionReturnDefinition("create_target_record", "other", null))
                ])
        ],
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
        [StringField("name")]);

    private static EntityDefinition ConsumerEntity() => new(
        "consumer",
        "operations",
        "consumer",
        "consumer",
        "consumer",
        new EntityDisplayDefinition("name", null),
        [
            StringField("name"),
            new FieldDefinition(
                "target",
                "target",
                "Target",
                "relation",
                "target_id",
                "bigint",
                false,
                false,
                "target",
                [],
                null)
        ]);

    private static FieldDefinition StringField(string id) => new(
        id,
        id,
        id,
        "string",
        id,
        "text",
        false,
        false,
        null,
        [],
        null);

    private static ActionStepDefinition Step(
        string id,
        string type,
        string? entity,
        ActionReturnDefinition? returned = null) => new(
        id,
        type,
        entity,
        [],
        null,
        null,
        new Dictionary<string, ActionValueDefinition>(),
        null,
        null,
        new Dictionary<string, ActionValueDefinition>(),
        null,
        null,
        new Dictionary<string, ActionValueDefinition>(),
        returned);
}
