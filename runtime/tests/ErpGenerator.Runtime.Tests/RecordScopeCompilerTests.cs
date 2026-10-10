using System.Text.Json;
using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RecordScopeCompilerTests
{
    [Fact]
    public void Compiler_preserves_canonical_typed_record_scopes_and_plans_governed_field_indexes()
    {
        var bundle = BundleWithGrants(
        [
            RecordGrant("read", "created_by", RecordPermissionScopeSources.ActorPrincipal),
            RecordGrant("create", "owner_principal", RecordPermissionScopeSources.ActorPrincipal),
            RecordGrant(
                "update",
                "employee",
                RecordPermissionScopeSources.LinkedRecord,
                linkedEntity: "employee"),
            RecordGrant(
                "archive",
                "warehouse",
                RecordPermissionScopeSources.LinkedRecord,
                linkedEntity: "employee",
                linkedField: "warehouse")
        ]);

        var compiled = new ErpBundleCompiler().Compile(bundle);
        var grants = compiled.Roles.Single(role => role.Id == "operator").Grants;

        grants.Should().Contain(grant =>
            grant.Scope == PermissionGrantScopes.Record &&
            grant.Action == "read" &&
            grant.Entity == "order" &&
            grant.Target == string.Empty &&
            grant.RecordScope == new RecordScopeDefinition(
                "created_by",
                RecordPermissionScopeSources.ActorPrincipal));
        grants.Should().Contain(grant =>
            grant.RecordScope == new RecordScopeDefinition(
                "owner_principal",
                RecordPermissionScopeSources.ActorPrincipal));
        grants.Should().Contain(grant =>
            grant.RecordScope == new RecordScopeDefinition(
                "employee",
                RecordPermissionScopeSources.LinkedRecord,
                "employee"));
        grants.Should().Contain(grant =>
            grant.RecordScope == new RecordScopeDefinition(
                "warehouse",
                RecordPermissionScopeSources.LinkedRecord,
                "employee",
                "warehouse"));

        var indexes = compiled.Tables.Single(table => table.TableName == "orders").Indexes;
        indexes.Should().Contain(index =>
            index.Name == "ix_orders_created_by_record_scope" &&
            index.Columns.SequenceEqual(new[] { "created_by" }) &&
            !index.IsUnique);
        indexes.Should().Contain(index =>
            index.Name == "ix_orders_owner_principal_record_scope" &&
            index.Columns.SequenceEqual(new[] { "owner_principal" }) &&
            !index.IsUnique);
        indexes.Should().Contain(index =>
            index.Name == "ix_orders_employee_id_record_scope" &&
            index.Columns.SequenceEqual(new[] { "employee_id" }) &&
            !index.IsUnique);
        indexes.Should().Contain(index =>
            index.Name == "ix_orders_warehouse_id_record_scope" &&
            index.Columns.SequenceEqual(new[] { "warehouse_id" }) &&
            !index.IsUnique);

        new RuntimeSqlBuilder().BuildDeploymentStatements(compiled).Should().Contain(statement =>
            statement.Contains(
                "create index if not exists \"ix_orders_owner_principal_record_scope\" on \"app\".\"orders\" (\"owner_principal\")",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Role_json_retains_typed_record_scope_without_changing_legacy_null_shape()
    {
        var compiled = new ErpBundleCompiler().Compile(BundleWithGrants(
        [
            RecordGrant("read", "created_by", RecordPermissionScopeSources.ActorPrincipal),
            new ErpGrant { Scope = PermissionGrantScopes.Entity, Action = "read", Entity = "warehouse" }
        ]));

        var json = JsonSerializer.Serialize(compiled.Roles.Single().Grants);
        using var document = JsonDocument.Parse(json);
        var recordGrant = document.RootElement.EnumerateArray().Single(element =>
            element.GetProperty("Scope").GetString() == PermissionGrantScopes.Record);
        recordGrant.GetProperty("RecordScope").GetProperty("Field").GetString().Should().Be("created_by");
        recordGrant.GetProperty("RecordScope").GetProperty("Source").GetString().Should().Be("actor_principal");
        document.RootElement.EnumerateArray().Single(element =>
                element.GetProperty("Scope").GetString() == PermissionGrantScopes.Entity)
            .TryGetProperty("RecordScope", out _).Should().BeFalse();

        JsonSerializer.Serialize(new ErpGrant { Action = "read", Entity = "warehouse" })
            .Should().NotContain("RecordScope", "legacy bundle hashes must not gain a null property");
    }

    [Fact]
    public void Migration_identity_includes_the_complete_typed_record_scope()
    {
        var compiler = new ErpBundleCompiler();
        var before = compiler.Compile(BundleWithGrants(
            [RecordGrant("read", "created_by", RecordPermissionScopeSources.ActorPrincipal)]));
        var afterBundle = BundleWithGrants(
            [RecordGrant("read", "owner_principal", RecordPermissionScopeSources.ActorPrincipal)],
            "1.0.1");
        var after = compiler.Compile(afterBundle);

        var plan = new ErpBundleMigrationPlanner().PlanMigration(before, after);

        plan.Operations.Should().Contain(operation =>
            operation.Type == "add_permission" &&
            operation.Field == "operator:record:read:order:owner_principal:actor_principal:-:-");
        plan.Operations.Should().Contain(operation =>
            operation.Type == "blocked_remove_permission" &&
            operation.Field == "operator:record:read:order:created_by:actor_principal:-:-");
    }

    [Fact]
    public void Record_scope_index_names_are_stable_and_bounded_to_postgres_identifiers()
    {
        var longEntity = $"entity_{new string('e', 56)}";
        var longTable = $"records_{new string('t', 55)}";
        var longField = $"principal_{new string('p', 53)}";
        var bundle = BundleWithGrants([]);
        bundle.Modules[0].Entities.Add(new ErpEntity
        {
            Id = longEntity,
            Name = longTable,
            DisplayName = "Long records",
            Fields =
            [
                new ErpField
                {
                    Id = longField,
                    Name = longField,
                    DisplayName = "Long principal",
                    Type = "integer"
                }
            ]
        });
        bundle.Roles["operator"].Grants.Add(new ErpGrant
        {
            Scope = PermissionGrantScopes.Record,
            Action = "read",
            Entity = longEntity,
            RecordScope = new ErpRecordScope
            {
                Field = longField,
                Source = RecordPermissionScopeSources.ActorPrincipal
            }
        });

        var first = new ErpBundleCompiler().Compile(bundle);
        var second = new ErpBundleCompiler().Compile(bundle);
        var firstIndex = first.Tables.Single(table => table.TableName == longTable).Indexes.Single();
        var secondIndex = second.Tables.Single(table => table.TableName == longTable).Indexes.Single();

        firstIndex.Name.Should().HaveLength(63);
        firstIndex.Name.Should().Be(secondIndex.Name);
        firstIndex.Name.Should().MatchRegex("_[0-9a-f]{12}$");
        firstIndex.Columns.Should().Equal(longField);
        firstIndex.IsUnique.Should().BeFalse();
    }

    [Fact]
    public void Compiler_rejects_unsafe_or_malformed_record_scope_variants()
    {
        var cases = new (string Label, ErpGrant Grant, string Expected)[]
        {
            ("wildcard entity", RecordGrant("read", "created_by", "actor_principal", entity: "*"), "concrete entity"),
            ("entity alias", RecordGrant("read", "created_by", "actor_principal", entity: "orders"), "unknown entity"),
            ("target predicate", RecordGrant("read", "created_by", "actor_principal", target: "created_by=current_user"), "must not declare target"),
            ("missing typed scope", new ErpGrant { Scope = "record", Action = "read", Entity = "order" }, "typed recordScope"),
            ("unsupported action", RecordGrant("admin", "created_by", "actor_principal"), "action 'admin' is not supported"),
            ("raw source", RecordGrant("read", "created_by", "created_by=current_user"), "source 'created_by=current_user' is not supported"),
            ("actor principal extra link", RecordGrant("read", "created_by", "actor_principal", linkedEntity: "employee"), "must not declare linkedEntity or linkedField"),
            ("unknown actor field", RecordGrant("read", "missing", "actor_principal"), "unknown field"),
            ("number actor field", RecordGrant("read", "owner_number", "actor_principal"), "noncomputed integer"),
            ("decimal actor field", RecordGrant("read", "owner_decimal", "actor_principal"), "noncomputed integer"),
            ("computed actor field", RecordGrant("read", "computed_principal", "actor_principal"), "noncomputed integer"),
            ("relation actor field", RecordGrant("read", "employee", "actor_principal"), "noncomputed integer"),
            ("nonrelation governed field", RecordGrant("read", "owner_principal", "linked_record", linkedEntity: "employee"), "noncomputed relation"),
            ("missing linked entity", RecordGrant("read", "warehouse", "linked_record"), "must declare linkedEntity"),
            ("noncanonical linked entity", RecordGrant("read", "warehouse", "linked_record", linkedEntity: "warehouses"), "exact canonical entity id"),
            ("wrong direct target", RecordGrant("read", "warehouse", "linked_record", linkedEntity: "employee"), "must exactly match governed relation target"),
            ("unknown linked field", RecordGrant("read", "warehouse", "linked_record", linkedEntity: "employee", linkedField: "missing"), "unknown linked field"),
            ("nonrelation linked field", RecordGrant("read", "warehouse", "linked_record", linkedEntity: "employee", linkedField: "name"), "exactly one declared noncomputed relation"),
            ("computed linked field", RecordGrant("read", "warehouse", "linked_record", linkedEntity: "employee", linkedField: "computed_warehouse"), "exactly one declared noncomputed relation"),
            ("wrong linked target", RecordGrant("read", "warehouse", "linked_record", linkedEntity: "employee", linkedField: "manager"), "instead of governed relation target"),
            ("second hop", RecordGrant("read", "warehouse", "linked_record", linkedEntity: "employee", linkedField: "warehouse.site"), "linkedField must be lower_snake_case")
        };

        foreach (var testCase in cases)
        {
            var compile = () => new ErpBundleCompiler().Compile(BundleWithGrants([testCase.Grant]));

            compile.Should().Throw<BundleCompilationException>(testCase.Label)
                .Which.Errors.Should().Contain(error =>
                    error.Contains(testCase.Expected, StringComparison.OrdinalIgnoreCase),
                    testCase.Label);
        }
    }

    [Fact]
    public void Compiler_accepts_only_the_closed_record_action_set()
    {
        var supported = new[]
        {
            "read",
            "create",
            "update",
            "archive",
            "restore",
            "start_workflow",
            "approve_task",
            "reject_task"
        };
        var compiled = new ErpBundleCompiler().Compile(BundleWithGrants(supported
            .Select(action => RecordGrant(action, "created_by", RecordPermissionScopeSources.ActorPrincipal))
            .ToList()));

        compiled.Roles.Single().Grants.Select(grant => grant.Action).Should().BeEquivalentTo(supported);

        foreach (var unsupported in new[] { "*", "admin", "read_task", "write", "view", "execute" })
        {
            var compile = () => new ErpBundleCompiler().Compile(BundleWithGrants(
                [RecordGrant(unsupported, "created_by", RecordPermissionScopeSources.ActorPrincipal)]));

            compile.Should().Throw<BundleCompilationException>()
                .Which.Errors.Should().Contain(error =>
                    error.Contains($"action '{unsupported}' is not supported", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Compiler_rejects_record_scope_payload_on_every_nonrecord_grant_kind()
    {
        foreach (var scope in new[]
                 {
                     PermissionGrantScopes.Entity,
                     PermissionGrantScopes.Field,
                     PermissionGrantScopes.Page,
                     PermissionGrantScopes.Action
                 })
        {
            var grant = new ErpGrant
            {
                Scope = scope,
                Action = scope switch
                {
                    PermissionGrantScopes.Page => "view",
                    PermissionGrantScopes.Action => "execute",
                    _ => "read"
                },
                Entity = "order",
                Target = scope switch
                {
                    PermissionGrantScopes.Field => "name",
                    PermissionGrantScopes.Page => "entity_list",
                    PermissionGrantScopes.Action => "missing_action",
                    _ => string.Empty
                },
                RecordScope = new ErpRecordScope
                {
                    Field = "created_by",
                    Source = RecordPermissionScopeSources.ActorPrincipal
                }
            };

            var errors = new ErpBundleCompiler().Validate(BundleWithGrants([grant]));

            errors.Should().Contain(error =>
                error.Contains("recordScope is valid only for scope 'record'", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Action_permission_requirements_cannot_embed_role_record_scopes()
    {
        var bundle = BundleWithGrants(
            [new ErpGrant { Scope = PermissionGrantScopes.Entity, Action = "read", Entity = "order" }]);
        bundle.Actions.Add(new ErpAction
        {
            Key = "close_order",
            Label = "Close order",
            Entity = "order",
            Type = "record_action",
            Requires = new ErpActionRequires
            {
                Permissions =
                [
                    RecordGrant("read", "created_by", RecordPermissionScopeSources.ActorPrincipal)
                ]
            }
        });

        var errors = new ErpBundleCompiler().Validate(bundle);

        errors.Should().Contain(error =>
            error.Contains("must not declare record-scoped permissions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Action_permission_requirements_reject_every_nonentity_scope_and_ignored_payload()
    {
        var cases = new (ErpGrant Requirement, string Expected)[]
        {
            (new ErpGrant { Scope = PermissionGrantScopes.Field, Action = "read", Entity = "order", Target = "name" }, "must use scope 'entity'"),
            (new ErpGrant { Scope = PermissionGrantScopes.Page, Action = "read", Entity = "order", Target = "dashboard" }, "must use scope 'entity'"),
            (new ErpGrant { Scope = PermissionGrantScopes.Action, Action = "read", Entity = "order", Target = "close_order" }, "must use scope 'entity'"),
            (new ErpGrant { Scope = PermissionGrantScopes.Entity, Action = "read", Entity = "order", Target = "name" }, "must not declare target"),
            (new ErpGrant
            {
                Scope = PermissionGrantScopes.Entity,
                Action = "read",
                Entity = "order",
                RecordScope = new ErpRecordScope
                {
                    Field = "created_by",
                    Source = RecordPermissionScopeSources.ActorPrincipal
                }
            }, "must not declare record-scoped permissions")
        };

        foreach (var (requirement, expected) in cases)
        {
            var bundle = BundleWithGrants(
                [new ErpGrant { Scope = PermissionGrantScopes.Entity, Action = "read", Entity = "order" }]);
            bundle.Actions.Add(ActionRequiring(requirement));

            var errors = new ErpBundleCompiler().Validate(bundle);

            errors.Should().Contain(error =>
                error.Contains(expected, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Yaml_loader_accepts_only_the_closed_typed_record_scope_shape()
    {
        var valid = new ErpBundleYamlLoader().Load(YamlWithRecordScope(
            """
            recordScope:
              field: created_by
              source: actor_principal
            """));
        new ErpBundleCompiler().Compile(valid).Roles.Single().Grants.Single().RecordScope.Should().Be(
            new RecordScopeDefinition("created_by", "actor_principal"));

        foreach (var (label, yaml, expected) in new[]
                 {
                     (
                         "raw expression",
                         YamlWithRecordScope("recordScope: created_by=current_user"),
                         "typed mapping"),
                     (
                         "unknown predicate",
                         YamlWithRecordScope(
                             """
                             recordScope:
                               field: created_by
                               source: actor_principal
                               predicate: created_by=current_user
                             """),
                         "unsupported member 'predicate'"),
                     (
                         "nested predicate",
                         YamlWithRecordScope(
                             """
                             recordScope:
                               field:
                                 eq: created_by
                               source: actor_principal
                             """),
                         "must be a scalar identifier"),
                     (
                         "sibling raw predicate",
                         YamlWithRecordScopeSibling("predicate", "created_by=current_user"),
                         "Record-scoped grant contains unsupported member 'predicate'"),
                     (
                         "sibling raw sql",
                         YamlWithRecordScopeSibling("sql", "created_by = current_user"),
                         "Record-scoped grant contains unsupported member 'sql'")
                 })
        {
            var load = () => new ErpBundleYamlLoader().Load(yaml);

            load.Should().Throw<BundleCompilationException>(label)
                .Which.Errors.Should().Contain(error =>
                    error.Contains(expected, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Yaml_record_scope_shape_validation_is_limited_to_role_grants()
    {
        const string bundleYaml = """
            id: record_scope_business_field
            name: Record scope business field
            version: 1.0.0
            modules:
              - id: ops
                name: Operations
                entities:
                  - id: order
                    name: orders
                    displayName: Orders
                    fields:
                      - id: record_scope
                        name: record_scope
                        displayName: Record scope
                        type: string
                        required: false
            actions:
              - key: classify_order
                label: Classify order
                entity: order
                type: record_action
                requires:
                  permissions:
                    - action: update
                      entity: order
                steps:
                  - id: set_record_scope
                    type: update_current_record
                    set:
                      record_scope:
                        value: internal
            roles:
              owner:
                displayName: Owner
                grants:
                  - action: "*"
                    entity: "*"
            """;
        var loader = new ErpBundleYamlLoader();

        var loaded = loader.Load(bundleYaml);

        loaded.Actions.Single().Steps.Single().Set.Should().ContainKey("record_scope");
        new ErpBundleCompiler().Compile(loaded).Actions.Should().ContainSingle();

        const string seedYaml = """
            seed:
              key: record_scope_seed
              label: Record scope seed
            records:
              - ref: order_one
                entity: order
                values:
                  record_scope: internal
            """;
        var seedPath = Path.Combine(Path.GetTempPath(), $"record-scope-seed-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(seedPath, seedYaml);

            var seed = await loader.LoadSeedFileAsync(seedPath);

            seed.Records.Single().Values["record_scope"].Should().Be("internal");
        }
        finally
        {
            File.Delete(seedPath);
        }
    }

    private static ErpGrant RecordGrant(
        string action,
        string field,
        string source,
        string entity = "order",
        string target = "",
        string linkedEntity = "",
        string linkedField = "") => new()
        {
            Scope = PermissionGrantScopes.Record,
            Action = action,
            Entity = entity,
            Target = target,
            RecordScope = new ErpRecordScope
            {
                Field = field,
                Source = source,
                LinkedEntity = linkedEntity,
                LinkedField = linkedField
            }
        };

    private static ErpAction ActionRequiring(ErpGrant requirement) => new()
    {
        Key = "inspect_order",
        Label = "Inspect order",
        Entity = "order",
        Type = "record_action",
        Requires = new ErpActionRequires { Permissions = [requirement] },
        Steps =
        [
            new ErpActionStep
            {
                Id = "audit_inspection",
                Type = "audit_event",
                Event = "order.inspected",
                Message = "Order inspected."
            }
        ]
    };

    private static ErpBundle BundleWithGrants(List<ErpGrant> grants, string version = "1.0.0") => new()
    {
        Id = "record_scope_bundle",
        Name = "Record scope bundle",
        Version = version,
        Modules =
        [
            new ErpModule
            {
                Id = "ops",
                Name = "Operations",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "warehouse",
                        Name = "warehouses",
                        DisplayName = "Warehouses",
                        Fields =
                        [
                            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "employee",
                        Name = "employees",
                        DisplayName = "Employees",
                        Fields =
                        [
                            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                            new ErpField { Id = "warehouse", Name = "warehouse_id", DisplayName = "Warehouse", Type = "relation", References = "warehouse" },
                            new ErpField
                            {
                                Id = "computed_warehouse",
                                Name = "computed_warehouse_id",
                                DisplayName = "Computed warehouse",
                                Type = "relation",
                                References = "warehouse",
                                Computed = new ErpComputedField
                                {
                                    Op = "coalesce",
                                    Left = new ErpComputedOperand { Field = "warehouse" },
                                    Right = new ErpComputedOperand { Value = 1 }
                                }
                            },
                            new ErpField { Id = "manager", Name = "manager_id", DisplayName = "Manager", Type = "relation", References = "employee" }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "order",
                        Name = "orders",
                        DisplayName = "Orders",
                        Fields =
                        [
                            new ErpField { Id = "name", Name = "name", DisplayName = "Name", Type = "string", Required = true },
                            new ErpField { Id = "owner_principal", Name = "owner_principal", DisplayName = "Owner principal", Type = "integer" },
                            new ErpField { Id = "owner_number", Name = "owner_number", DisplayName = "Owner number", Type = "number" },
                            new ErpField { Id = "owner_decimal", Name = "owner_decimal", DisplayName = "Owner decimal", Type = "decimal" },
                            new ErpField
                            {
                                Id = "computed_principal",
                                Name = "computed_principal",
                                DisplayName = "Computed principal",
                                Type = "integer",
                                Computed = new ErpComputedField
                                {
                                    Op = "add",
                                    Left = new ErpComputedOperand { Field = "owner_principal" },
                                    Right = new ErpComputedOperand { Value = 0 }
                                }
                            },
                            new ErpField { Id = "employee", Name = "employee_id", DisplayName = "Employee", Type = "relation", References = "employee" },
                            new ErpField { Id = "warehouse", Name = "warehouse_id", DisplayName = "Warehouse", Type = "relation", References = "warehouse" }
                        ]
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["operator"] = new ErpRole
            {
                DisplayName = "Operator",
                Grants = grants
            }
        }
    };

    private static string YamlWithRecordScope(string recordScope) =>
        $$"""
        id: record_scope_yaml
        name: Record scope YAML
        version: 1.0.0
        modules:
          - id: ops
            name: Operations
            entities:
              - id: order
                name: orders
                displayName: Orders
                fields:
                  - id: name
                    name: name
                    displayName: Name
                    type: string
                    required: true
        roles:
          operator:
            displayName: Operator
            grants:
              - scope: record
                action: read
                entity: order
                {{recordScope.Replace("\n", "\n                ", StringComparison.Ordinal)}}
        """;

    private static string YamlWithRecordScopeSibling(string member, string value) =>
        $$"""
        id: record_scope_yaml
        name: Record scope YAML
        version: 1.0.0
        modules:
          - id: ops
            name: Operations
            entities:
              - id: order
                name: orders
                displayName: Orders
                fields:
                  - id: name
                    name: name
                    displayName: Name
                    type: string
                    required: true
        roles:
          operator:
            displayName: Operator
            grants:
              - scope: record
                action: read
                entity: order
                recordScope:
                  field: created_by
                  source: actor_principal
                {{member}}: {{value}}
        """;
}
