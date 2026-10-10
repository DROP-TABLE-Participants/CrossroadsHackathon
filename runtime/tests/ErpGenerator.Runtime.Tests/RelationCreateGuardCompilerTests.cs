using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class RelationCreateGuardCompilerTests
{
    [Fact]
    public void Canonical_field_ids_compile_into_closed_runtime_metadata()
    {
        var compiled = new ErpBundleCompiler().Compile(Bundle(
            relationField: "work_order_ref",
            referencedField: "state_code"));

        compiled.Entities.Single(entity => entity.Id == "completion")
            .RelationCreateGuards.Should().ContainSingle().Which.Should().BeEquivalentTo(
                new RelationCreateGuardDefinition(
                    "work_order_ref",
                    "work_order",
                    "state_code",
                    "eq",
                    new TypedLiteralDefinition("string", "open")));
    }

    [Theory]
    [InlineData("work_order_id", "state_code")]
    [InlineData("work_order_ref", "status")]
    public void Field_names_are_not_accepted_as_relation_guard_id_aliases(
        string relationField,
        string referencedField)
    {
        var compile = () => new ErpBundleCompiler().Compile(Bundle(relationField, referencedField));

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Contains("relation_create_guard", StringComparison.Ordinal) &&
                error.Contains("must resolve", StringComparison.Ordinal));
    }

    [Fact]
    public void Yaml_loader_preserves_typed_relation_create_guard_fields()
    {
        var bundle = new ErpBundleYamlLoader().Load(
            """
            id: guarded_bundle
            name: Guarded bundle
            version: 1.0.0
            modules:
              - id: operations
                name: Operations
                entities:
                  - id: work_order
                    name: work_orders
                    displayName: Work orders
                    fields:
                      - id: title
                        name: title
                        displayName: Title
                        type: string
                        required: true
                      - id: state_code
                        name: status
                        displayName: Status
                        type: enum
                        required: true
                        options: [open, closed]
                  - id: completion
                    name: completions
                    displayName: Completions
                    fields:
                      - id: note
                        name: note
                        displayName: Note
                        type: string
                        required: true
                      - id: work_order_ref
                        name: work_order_id
                        displayName: Work order
                        type: relation
                        required: true
                        references: work_order
                    relation_create_guards:
                      - relation_field: work_order_ref
                        referenced_field: state_code
                        op: eq
                        value:
                          type: string
                          value: open
            roles:
              owner:
                displayName: Owner
                grants:
                  - action: '*'
                    entity: '*'
            """);

        var guard = new ErpBundleCompiler().Compile(bundle)
            .Entities.Single(entity => entity.Id == "completion")
            .RelationCreateGuards.Should().ContainSingle().Which;
        guard.RelationField.Should().Be("work_order_ref");
        guard.ReferencedField.Should().Be("state_code");
        guard.Value.Should().Be(new TypedLiteralDefinition("string", "open"));
    }

    private static ErpBundle Bundle(string relationField, string referencedField) => new()
    {
        Id = "guarded_bundle",
        Name = "Guarded bundle",
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
                        Id = "work_order",
                        Name = "work_orders",
                        DisplayName = "Work orders",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "title", Name = "title", DisplayName = "Title",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "state_code", Name = "status", DisplayName = "Status",
                                Type = "enum", Required = true, Options = ["open", "closed"]
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "completion",
                        Name = "completions",
                        DisplayName = "Completions",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "note", Name = "note", DisplayName = "Note",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "work_order_ref", Name = "work_order_id", DisplayName = "Work order",
                                Type = "relation", Required = true, References = "work_order"
                            }
                        ],
                        RelationCreateGuards =
                        [
                            new ErpRelationCreateGuard
                            {
                                RelationField = relationField,
                                ReferencedField = referencedField,
                                Op = "eq",
                                Value = new ErpTypedLiteral { Type = "string", Value = "open" }
                            }
                        ]
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["owner"] = new()
            {
                DisplayName = "Owner",
                Grants = [new ErpGrant { Action = "*", Entity = "*" }]
            }
        }
    };
}
