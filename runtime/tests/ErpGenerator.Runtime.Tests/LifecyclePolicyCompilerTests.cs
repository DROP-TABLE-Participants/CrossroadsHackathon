using System.Text.Json;
using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class LifecyclePolicyCompilerTests
{
    [Fact]
    public void Nullable_lifecycle_preserves_legacy_json_shape_and_present_policy_is_hash_bound()
    {
        JsonSerializer.Serialize(new ErpField
        {
            Id = "status",
            Name = "status",
            DisplayName = "Status",
            Type = "enum",
            Required = true,
            Options = ["Draft", "Approved"]
        })
            .Should().NotContain("Lifecycle", "legacy bundle hashes must not gain a null property");

        var compiler = new ErpBundleCompiler();
        var legacy = compiler.Compile(LifecycleBundle(includeLifecycle: false));
        var lifecycle = compiler.Compile(LifecycleBundle());
        lifecycle.BundleHash.Should().NotBe(legacy.BundleHash);
    }

    [Fact]
    public void Yaml_lifecycle_contract_compiles_to_canonical_field_metadata()
    {
        var bundle = new ErpBundleYamlLoader().Load(
            """
            id: lifecycle_bundle
            name: Lifecycle bundle
            version: 1.0.0
            modules:
              - id: production
                name: Production
                entities:
                  - id: formula
                    name: formulas
                    displayName: Formulas
                    fields:
                      - id: name
                        name: name
                        displayName: Name
                        type: string
                        required: true
                      - id: status
                        name: status
                        displayName: Status
                        type: enum
                        required: true
                        options: [Draft, Approved]
                        lifecycle:
                          initial: draft
                          transitions: named_action
            actions:
              - key: approve_formula
                label: Approve formula
                entity: formula
                type: record_action
                requires:
                  permissions:
                    - entity: formula
                      action: read
                    - entity: formula
                      action: effect_update
                steps:
                  - id: mark_approved
                    type: update_current_record
                    set:
                      status:
                        value: Approved
            roles:
              manager:
                displayName: Manager
                grants:
                  - entity: formula
                    action: read
                  - entity: formula
                    action: effect_update
                  - entity: formula
                    scope: field
                    target: status
                    action: effect_write
                  - entity: formula
                    scope: action
                    target: approve_formula
                    action: execute
            """);

        var compiled = new ErpBundleCompiler().Compile(bundle);

        var lifecycle = compiled.Entities.Single().Fields.Single(field => field.Id == "status").Lifecycle;
        lifecycle.Should().Be(new FieldLifecycleDefinition("Draft", "named_action"));
    }

    [Theory]
    [InlineData("lifecyle:\n  initial: Draft\n  transitions: direct", "lifecycle-like")]
    [InlineData("lifecyclePolicy:\n  initial: Draft\n  transitions: direct", "lifecycle-like")]
    [InlineData("fieldLifecycle:\n  initial: Draft\n  transitions: direct", "lifecycle-like")]
    [InlineData("lifecycle:\n  initial: Draft\n  transition: direct", "unsupported member")]
    [InlineData("lifecycle: direct", "typed mapping")]
    public void Yaml_lifecycle_shape_rejects_unknown_or_misspelled_members(
        string fieldFragment,
        string expectedError)
    {
        var load = () => new ErpBundleYamlLoader().Load(LifecycleYaml(fieldFragment));

        var exception = load.Should().Throw<BundleCompilationException>().Which;
        exception.Errors.Should().Contain(error =>
            error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Yaml_lifecycle_shape_rejects_duplicate_keys()
    {
        var yaml = LifecycleYaml(
            "lifecycle:\n  initial: Draft\n  transitions: direct\nlifecycle:\n  initial: Draft\n  transitions: named_action");

        var load = () => new ErpBundleYamlLoader().Load(yaml);

        load.Should().Throw<Exception>("duplicate lifecycle policy cannot be accepted by the permissive bundle deserializer");
    }

    [Fact]
    public void Yaml_field_extensions_remain_compatible_outside_the_typed_lifecycle_contract()
    {
        var bundle = new ErpBundleYamlLoader().Load(LifecycleYaml(
            "extensionHint: future-compatible\nlifecycle:\n  initial: Draft\n  transitions: direct"));

        bundle.Modules.Single().Entities.Single().Fields.Single().Lifecycle.Should()
            .BeEquivalentTo(new ErpFieldLifecycle { Initial = "Draft", Transitions = "direct" });
    }

    [Theory]
    [InlineData("non_enum", "must use type 'enum'")]
    [InlineData("optional", "must be required")]
    [InlineData("unique", "must not be unique")]
    [InlineData("computed", "must not be computed")]
    [InlineData("unknown_initial", "must match one declared enum option")]
    [InlineData("unknown_transitions", "transitions must be 'direct' or 'named_action'")]
    public void Lifecycle_shape_is_strict(string invalidCase, string expectedError)
    {
        var bundle = LifecycleBundle(
            fieldType: invalidCase == "non_enum" ? "string" : "enum",
            required: invalidCase != "optional",
            unique: invalidCase == "unique",
            computed: invalidCase == "computed",
            initial: invalidCase == "unknown_initial" ? "Missing" : "Draft",
            transitions: invalidCase == "unknown_transitions" ? "workflow" : "named_action");

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error =>
            error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Malformed_null_lifecycle_transitions_returns_a_validation_error_without_throwing()
    {
        var bundle = LifecycleBundle(transitions: null!);

        var validate = () => new ErpBundleCompiler().Validate(bundle);

        validate.Should().NotThrow();
        validate().Should().Contain(error => error.Contains(
            "transitions must be 'direct' or 'named_action'",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Whitespace_padded_named_action_cannot_bypass_canonical_shape_or_semantic_restrictions()
    {
        var bundle = LifecycleBundle(
            transitions: " named_action ",
            actionPermission: "update",
            fieldGrantAction: "write");
        bundle.Modules[0].Workflows.Add(new ErpWorkflow
        {
            Id = "whitespace_lifecycle_workflow",
            Name = "Whitespace lifecycle workflow",
            Entity = "formula",
            Trigger = new ErpWorkflowTrigger { Action = "submit_formula" },
            Steps =
            [
                new ErpWorkflowStep
                {
                    Id = "blank_status",
                    Type = "update_record",
                    Set = new Dictionary<string, string> { ["status"] = "   " }
                }
            ]
        });

        var errors = new ErpBundleCompiler().Validate(bundle);

        errors.Should().Contain(error =>
            error.Contains("transitions must be 'direct' or 'named_action'", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("cannot contain surrounding whitespace", StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains(
            "cannot set named-action lifecycle field 'formula.status'",
            StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains(
            "only with exact permission 'effect_update'",
            StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains(
            "must not receive direct field write authority",
            StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains(
            "no executable role closure",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_action_cannot_supply_a_server_initialized_lifecycle_field()
    {
        var bundle = LifecycleBundle();
        bundle.Actions.Add(new ErpAction
        {
            Key = "copy_formula",
            Label = "Copy formula",
            Entity = "formula",
            Type = "record_action",
            Requires = new ErpActionRequires
            {
                Permissions =
                [
                    new ErpGrant { Entity = "formula", Action = "read" },
                    new ErpGrant { Entity = "formula", Action = "effect_create" }
                ]
            },
            Steps =
            [
                new ErpActionStep
                {
                    Id = "create_copy",
                    Type = "create_record",
                    Entity = "formula",
                    Values = new Dictionary<string, ErpActionValue>
                    {
                        ["name"] = new() { Value = "Copy" },
                        ["status"] = new() { Value = "Approved" }
                    }
                }
            ]
        });
        bundle.Roles["manager"].Grants.AddRange(
        [
            new ErpGrant { Entity = "formula", Action = "effect_create" },
            new ErpGrant { Entity = "formula", Scope = "field", Target = "name", Action = "effect_write" },
            new ErpGrant { Entity = "formula", Scope = "action", Target = "copy_formula", Action = "execute" }
        ]);

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error =>
            error.Contains(
                "cannot set server-initialized lifecycle field 'status'",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Named_action_lifecycle_update_requires_effect_update_not_direct_update()
    {
        var bundle = LifecycleBundle(actionPermission: "update", fieldGrantAction: "write");

        var errors = new ErpBundleCompiler().Validate(bundle);

        errors.Should().Contain(error => error.Contains(
            "only with exact permission 'effect_update'",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Workflow_cannot_transition_a_named_action_lifecycle_field()
    {
        var bundle = LifecycleBundle();
        bundle.Modules[0].Workflows.Add(new ErpWorkflow
        {
            Id = "legacy_approval",
            Name = "Legacy approval",
            Entity = "formula",
            Trigger = new ErpWorkflowTrigger { Action = "submit_formula" },
            Steps =
            [
                new ErpWorkflowStep
                {
                    Id = "mark_approved",
                    Type = "update_record",
                    Set = new Dictionary<string, string> { ["status"] = "Approved" }
                }
            ]
        });

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error => error.Contains(
            "cannot set named-action lifecycle field 'formula.status'",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Named_action_lifecycle_field_rejects_direct_field_write_grants()
    {
        var bundle = LifecycleBundle();
        bundle.Roles["manager"].Grants.Add(new ErpGrant
        {
            Entity = "formula",
            Scope = "field",
            Target = "status",
            Action = "write"
        });

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error => error.Contains(
            "must not receive direct field write authority",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Named_action_lifecycle_requires_at_least_one_noninitial_option()
    {
        var bundle = LifecycleBundle();
        var status = bundle.Modules.Single().Entities.Single().Fields.Single(field => field.Id == "status");
        status.Options.RemoveAll(option => !option.Equals("Draft", StringComparison.OrdinalIgnoreCase));

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error => error.Contains(
            "must declare at least one noninitial enum option",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Named_action_lifecycle_requires_a_typed_noninitial_transition_action()
    {
        var bundle = LifecycleBundle();
        bundle.Actions.Clear();
        bundle.Roles["manager"].Grants.RemoveAll(grant =>
            grant.Scope.Equals(PermissionGrantScopes.Action, StringComparison.OrdinalIgnoreCase));

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error => error.Contains(
            "requires at least one typed action that assigns a declared noninitial enum option",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Named_action_lifecycle_does_not_count_an_action_that_only_reassigns_the_initial_option()
    {
        var bundle = LifecycleBundle();
        bundle.Actions.Single().Steps.Single().Set["status"] = new ErpActionValue { Value = "Draft" };

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error => error.Contains(
            "requires at least one typed action that assigns a declared noninitial enum option",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Named_action_lifecycle_does_not_coerce_a_boolean_into_an_ambiguous_enum_string()
    {
        var bundle = LifecycleBundle();
        var status = bundle.Modules.Single().Entities.Single().Fields.Single(field => field.Id == "status");
        status.Options.Clear();
        status.Options.AddRange(["Draft", "true"]);
        bundle.Actions.Single().Steps.Single().Set["status"] = new ErpActionValue { Value = true };

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error => error.Contains(
            "requires at least one typed action that assigns a declared noninitial enum option",
            StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("action_source_read")]
    [InlineData("action_effect_update")]
    [InlineData("role_source_read")]
    [InlineData("role_named_action")]
    [InlineData("role_effect_update")]
    [InlineData("role_effect_write")]
    [InlineData("split_across_roles")]
    public void Named_action_lifecycle_requires_one_complete_action_and_role_authority_closure(string missing)
    {
        var bundle = LifecycleBundle();
        var action = bundle.Actions.Single();
        var manager = bundle.Roles["manager"];
        switch (missing)
        {
            case "action_source_read":
                action.Requires.Permissions.RemoveAll(grant =>
                    grant.Action.Equals("read", StringComparison.OrdinalIgnoreCase));
                break;
            case "action_effect_update":
                action.Requires.Permissions.RemoveAll(grant =>
                    grant.Action.Equals(ActionPermissionEffectAnalyzer.EffectUpdate, StringComparison.OrdinalIgnoreCase));
                break;
            case "role_source_read":
                manager.Grants.RemoveAll(grant =>
                    grant.Action.Equals("read", StringComparison.OrdinalIgnoreCase));
                break;
            case "role_named_action":
                manager.Grants.RemoveAll(grant =>
                    grant.Scope.Equals(PermissionGrantScopes.Action, StringComparison.OrdinalIgnoreCase));
                break;
            case "role_effect_update":
                manager.Grants.RemoveAll(grant =>
                    grant.Action.Equals(ActionPermissionEffectAnalyzer.EffectUpdate, StringComparison.OrdinalIgnoreCase));
                break;
            case "role_effect_write":
                manager.Grants.RemoveAll(grant =>
                    grant.Action.Equals(GranularPermissionActions.EffectWrite, StringComparison.OrdinalIgnoreCase));
                break;
            case "split_across_roles":
                manager.Grants.RemoveAll(grant =>
                    grant.Action.Equals(GranularPermissionActions.EffectWrite, StringComparison.OrdinalIgnoreCase));
                bundle.Roles["field_writer_only"] = new ErpRole
                {
                    DisplayName = "Field writer only",
                    Grants =
                    [
                        new ErpGrant
                        {
                            Entity = "formula",
                            Scope = PermissionGrantScopes.Field,
                            Target = "status",
                            Action = GranularPermissionActions.EffectWrite
                        }
                    ]
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(missing), missing, null);
        }

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error => error.Contains(
            "no executable role closure",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Named_action_lifecycle_compiles_with_multiple_independently_executable_roles()
    {
        var bundle = LifecycleBundle();
        bundle.Roles["quality_manager"] = CompleteLifecycleRole("Quality manager");
        bundle.Roles["release_manager"] = CompleteLifecycleRole("Release manager");

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().NotThrow();
    }

    [Fact]
    public void Lifecycle_policy_changes_are_reviewable_and_new_required_fields_still_require_backfill()
    {
        var compiler = new ErpBundleCompiler();
        var namedAction = compiler.Compile(LifecycleBundle(version: "1.0.0"));
        var namedEntity = namedAction.Entities.Single();
        var direct = namedAction with
        {
            Version = "1.1.0",
            Entities =
            [
                namedEntity with
                {
                    Fields = namedEntity.Fields.Select(field => field.Id == "status"
                        ? field with { Lifecycle = new FieldLifecycleDefinition("Draft", "direct") }
                        : field).ToArray()
                }
            ]
        };

        var widening = new ErpBundleMigrationPlanner().PlanMigration(namedAction, direct);
        widening.RiskLevel.Should().Be("warning");
        widening.Operations.Should().Contain(operation =>
            operation.Type == "review_lifecycle_privilege_widening" &&
            operation.Entity == "formula" &&
            operation.Field == "status");

        var fromEntity = namedAction.Entities.Single() with
        {
            Fields = namedAction.Entities.Single().Fields.Where(field => field.Id != "status").ToArray()
        };
        var withoutStatus = namedAction with
        {
            Version = "0.9.0",
            Entities = [fromEntity]
        };
        var requiredAddition = new ErpBundleMigrationPlanner().PlanMigration(withoutStatus, namedAction);
        requiredAddition.IsBlocked.Should().BeTrue();
        requiredAddition.Operations.Should().Contain(operation =>
            operation.Type == "blocked_required_field_without_default" &&
            operation.Entity == "formula" &&
            operation.Field == "status");
    }

    [Fact]
    public void Renderer_omits_lifecycle_on_create_and_disables_named_action_lifecycle_on_edit()
    {
        var status = new RuntimeFieldDefinition(
            "status",
            "formula",
            "status",
            "Status",
            "enum",
            "status",
            "text",
            true,
            false,
            null,
            ["Draft", "Approved"],
            null,
            new RuntimeFieldLifecycle("Draft", "named_action"));
        var name = new RuntimeFieldDefinition(
            "name", "formula", "name", "Name", "string", "name", "text", true, false, null, [], null);
        var entity = new RuntimeEntityDefinition(
            "formulas", "formula", "formulas", "Formulas", "app", "formulas", "name", null, [name, status]);
        var tenant = RuntimeTestTenant.Create("lifecycle-ui", "lifecycle-ui.erpgenerator.example", "unused");
        var session = new RuntimeSession(
            tenant.TenantStableId,
            tenant.Host,
            1,
            "owner@example.test",
            "owner",
            DateTimeOffset.UtcNow.AddHours(1));
        var permissions = new RuntimeEntityUiPermissions(true, true, true, true, true);

        var create = RuntimeEntityFormRenderer.RenderEntityForm(
            tenant, session, [entity], entity, null, new Dictionary<string, IReadOnlyList<RuntimeLookupItem>>(), permissions, [], []);
        var edit = RuntimeEntityFormRenderer.RenderEntityForm(
            tenant,
            session,
            [entity],
            entity,
            new Dictionary<string, object?> { ["id"] = 7L, ["name"] = "Formula", ["status"] = "Draft" },
            new Dictionary<string, IReadOnlyList<RuntimeLookupItem>>(),
            permissions,
            [],
            []);

        create.Should().NotContain("data-field=\"status\"");
        edit.Should().Contain("data-field=\"status\"");
        edit.Should().Contain("name=\"status\" required disabled");
        edit.Should().Contain("[name]:not(:disabled)");
    }

    private static ErpBundle LifecycleBundle(
        string version = "1.0.0",
        string fieldType = "enum",
        bool required = true,
        bool unique = false,
        bool computed = false,
        string initial = "Draft",
        string transitions = "named_action",
        string actionPermission = "effect_update",
        string fieldGrantAction = "effect_write",
        bool includeLifecycle = true)
    {
        var status = new ErpField
        {
            Id = "status",
            Name = "status",
            DisplayName = "Status",
            Type = fieldType,
            Required = required,
            Unique = unique,
            Options = ["Draft", "Approved"],
            Computed = computed
                ? new ErpComputedField
                {
                    Op = "coalesce",
                    Left = new ErpComputedOperand { Value = "Draft" },
                    Right = new ErpComputedOperand { Value = "Approved" }
                }
                : null,
            Lifecycle = includeLifecycle
                ? new ErpFieldLifecycle
                {
                    Initial = initial,
                    Transitions = transitions
                }
                : null
        };

        return new ErpBundle
        {
            Id = "lifecycle_bundle",
            Name = "Lifecycle bundle",
            Version = version,
            Modules =
            [
                new ErpModule
                {
                    Id = "production",
                    Name = "Production",
                    Entities =
                    [
                        new ErpEntity
                        {
                            Id = "formula",
                            Name = "formulas",
                            DisplayName = "Formulas",
                            Display = new ErpEntityDisplay { Primary = "name" },
                            Fields =
                            [
                                new ErpField
                                {
                                    Id = "name",
                                    Name = "name",
                                    DisplayName = "Name",
                                    Type = "string",
                                    Required = true
                                },
                                status
                            ]
                        }
                    ]
                }
            ],
            Actions =
            [
                new ErpAction
                {
                    Key = "approve_formula",
                    Label = "Approve formula",
                    Entity = "formula",
                    Type = "record_action",
                    Requires = new ErpActionRequires
                    {
                        Permissions =
                        [
                            new ErpGrant { Entity = "formula", Action = "read" },
                            new ErpGrant { Entity = "formula", Action = actionPermission }
                        ]
                    },
                    Steps =
                    [
                        new ErpActionStep
                        {
                            Id = "mark_approved",
                            Type = "update_current_record",
                            Set = new Dictionary<string, ErpActionValue>
                            {
                                ["status"] = new() { Value = "Approved" }
                            }
                        }
                    ]
                }
            ],
            Roles = new Dictionary<string, ErpRole>
            {
                ["manager"] = new()
                {
                    DisplayName = "Manager",
                    Grants =
                    [
                        new ErpGrant { Entity = "formula", Action = "read" },
                        new ErpGrant { Entity = "formula", Action = actionPermission },
                        new ErpGrant
                        {
                            Entity = "formula",
                            Scope = "field",
                            Target = "status",
                            Action = fieldGrantAction
                        },
                        new ErpGrant
                        {
                            Entity = "formula",
                            Scope = "action",
                            Target = "approve_formula",
                            Action = "execute"
                        }
                    ]
                }
            }
        };
    }

    private static ErpRole CompleteLifecycleRole(string displayName) =>
        new()
        {
            DisplayName = displayName,
            Grants =
            [
                new ErpGrant { Entity = "formula", Action = "read" },
                new ErpGrant { Entity = "formula", Action = ActionPermissionEffectAnalyzer.EffectUpdate },
                new ErpGrant
                {
                    Entity = "formula",
                    Scope = PermissionGrantScopes.Field,
                    Target = "status",
                    Action = GranularPermissionActions.EffectWrite
                },
                new ErpGrant
                {
                    Entity = "formula",
                    Scope = PermissionGrantScopes.Action,
                    Target = "approve_formula",
                    Action = GranularPermissionActions.Execute
                }
            ]
        };

    private static string LifecycleYaml(string fieldFragment)
    {
        var indentedFragment = string.Join(
            Environment.NewLine,
            fieldFragment.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => $"            {line}"));
        return $$"""
        id: lifecycle_yaml_shape
        name: Lifecycle YAML shape
        version: 1.0.0
        modules:
          - id: core
            name: Core
            entities:
              - id: document
                name: documents
                displayName: Documents
                fields:
                  - id: status
                    name: status
                    displayName: Status
                    type: enum
                    required: true
                    options: [Draft, Approved]
        {{indentedFragment}}
        """;
    }
}
