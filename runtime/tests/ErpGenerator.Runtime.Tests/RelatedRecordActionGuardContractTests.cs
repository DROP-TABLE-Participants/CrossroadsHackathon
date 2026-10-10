using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class RelatedRecordActionGuardContractTests
{
    [Fact]
    public void Compiler_accepts_typed_related_record_identity_guard()
    {
        var compiled = new ErpBundleCompiler().Compile(RelatedRecordActionGuardTestBundle.Create());

        var action = compiled.Actions.Should().ContainSingle().Subject;
        action.Permissions.Should().Contain(requirement =>
            requirement.Action == "read" && requirement.Entity == "formula_revision");
        action.Permissions.Should().Contain(requirement =>
            requirement.Action == "update" && requirement.Entity == "batch");
        var query = action.Steps.Should().ContainSingle(step => step.Type == "query_records").Subject;
        query.Entity.Should().Be("formula_revision");
        query.Where.Should().SatisfyRespectively(
            identity =>
            {
                identity.Field.Should().Be("id");
                identity.Op.Should().Be("eq");
                identity.ValueFrom!.Record.Should().Be("formula_revision");
            },
            approval =>
            {
                approval.Field.Should().Be("status");
                approval.Op.Should().Be("eq");
                approval.Value.Should().Be("approved");
            });
        var guard = action.Steps.Should().ContainSingle(step => step.Type == "guard").Subject;
        guard.Condition!.Left!.ResultCount.Should().Be(query.Id);
        Convert.ToInt64(guard.Condition.Right!.Value).Should().Be(1);
        action.Steps.Should().ContainSingle(step => step.Type == "update_current_record");
    }

    [Fact]
    public void Compiler_rejects_unsafe_or_identity_mismatched_system_id_filters()
    {
        var cases = new (ErpActionFilter Filter, string Error)[]
        {
            (
                RelatedRecordActionGuardTestBundle.IdentityFilter(
                    op: "gte",
                    recordField: "formula_revision"),
                "system id filter must use exact operator 'eq'"),
            (
                RelatedRecordActionGuardTestBundle.IdentityFilter(
                    op: "eq",
                    recordField: "specification"),
                "assigns relation target 'specification' to incompatible relation field 'id' targeting 'formula_revision'"),
            (
                RelatedRecordActionGuardTestBundle.IdentityFilter(
                    op: "eq",
                    recordField: "name"),
                "assigns non-relation field 'name' to relation field 'id'"),
            (
                new ErpActionFilter { Field = "id", Op = "eq", Value = "1" },
                "system id filter must use one relation-bound value_from.record identity; literal ids are forbidden"),
            (
                new ErpActionFilter { Field = "id", Op = "eq", Value = 1L },
                "system id filter must use one relation-bound value_from.record identity; literal ids are forbidden"),
            (
                new ErpActionFilter { Field = "id", Op = "in", Values = [1L, 2L] },
                "system id filter must compare one exact identity, not a value list")
        };

        foreach (var (filter, expectedError) in cases)
        {
            var errors = new ErpBundleCompiler().Validate(
                RelatedRecordActionGuardTestBundle.Create(filter));

            errors.Should().Contain(error => error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Compiler_rejects_computed_query_filter_target_before_runtime_sql()
    {
        var bundle = OrderedComparisonBundle("number", "number", targetComputed: true);

        var errors = new ErpBundleCompiler().Validate(bundle);

        errors.Should().Contain(error => error.Contains(
            "filter cannot target computed field 'formula_revision.target_value'",
            StringComparison.OrdinalIgnoreCase));
        errors.Should().Contain(error => error.Contains(
            "query_records filters require a materialized database field",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_accepts_exact_materialized_ordered_record_comparisons()
    {
        var compatibleTypes = new (string Target, string Source)[]
        {
            ("integer", "integer"),
            ("number", "number"),
            ("number", "decimal"),
            ("decimal", "number"),
            ("money", "money"),
            ("date", "date"),
            ("datetime", "datetime")
        };

        foreach (var (target, source) in compatibleTypes)
        {
            var errors = new ErpBundleCompiler().Validate(OrderedComparisonBundle(target, source));

            errors.Should().BeEmpty($"{target} and {source} are the same exact ordered scalar after deliberate decimal normalization");
        }
    }

    [Fact]
    public void Compiler_rejects_computed_identity_non_scalar_and_mismatched_ordered_record_sources()
    {
        var invalid = new (ErpBundle Bundle, string Error)[]
        {
            (
                OrderedComparisonBundle("number", "number", sourceComputed: true),
                "value_from.record field 'batch.source_value' must be materialized and noncomputed"),
            (
                OrderedComparisonBundle("number", "number", useSourceIdentity: true),
                "value_from.record cannot use system identity 'id'"),
            (
                OrderedComparisonBundle("integer", "number"),
                "ordered comparison requires exact compatible scalar types after normalization"),
            (
                OrderedComparisonBundle("date", "datetime"),
                "ordered comparison requires exact compatible scalar types after normalization"),
            (
                OrderedComparisonBundle("money", "number"),
                "ordered comparison requires exact compatible scalar types after normalization"),
            (
                OrderedComparisonBundle("string", "string"),
                "must use integer, number, decimal, money, date, or datetime, not 'string'"),
            (
                OrderedComparisonBundle("enum", "enum"),
                "must use integer, number, decimal, money, date, or datetime, not 'enum'"),
            (
                OrderedComparisonBundle("boolean", "boolean"),
                "must use integer, number, decimal, money, date, or datetime, not 'boolean'"),
            (
                OrderedComparisonBundle("relation", "relation"),
                "must use integer, number, decimal, money, date, or datetime, not 'relation'")
        };

        foreach (var (bundle, expectedError) in invalid)
        {
            var errors = new ErpBundleCompiler().Validate(bundle);

            errors.Should().Contain(error => error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Compiler_preserves_exact_eq_relation_correlation_behavior()
    {
        new ErpBundleCompiler().Validate(RelatedRecordActionGuardTestBundle.Create())
            .Should().BeEmpty();
    }

    private static ErpBundle OrderedComparisonBundle(
        string targetType,
        string sourceType,
        bool targetComputed = false,
        bool sourceComputed = false,
        bool useSourceIdentity = false)
    {
        var bundle = RelatedRecordActionGuardTestBundle.Create();
        var entities = bundle.Modules.Single().Entities;
        var targetEntity = entities.Single(entity => entity.Id == "formula_revision");
        var sourceEntity = entities.Single(entity => entity.Id == "batch");
        AddComparisonField(targetEntity, "target_value", targetType, targetComputed);
        AddComparisonField(sourceEntity, "source_value", sourceType, sourceComputed);
        bundle.Actions.Single().Steps.Single(step => step.Type == "query_records").Where.Add(
            new ErpActionFilter
            {
                Field = "target_value",
                Op = "gte",
                ValueFrom = new ErpActionValueFrom
                {
                    Record = useSourceIdentity ? "id" : "source_value"
                }
            });
        return bundle;
    }

    private static void AddComparisonField(
        ErpEntity entity,
        string fieldId,
        string type,
        bool computed)
    {
        if (computed)
        {
            entity.Fields.Add(new ErpField
            {
                Id = $"{fieldId}_base",
                Name = $"{fieldId}_base",
                DisplayName = $"{fieldId} base",
                Type = type
            });
        }

        entity.Fields.Add(new ErpField
        {
            Id = fieldId,
            Name = type.Equals("relation", StringComparison.OrdinalIgnoreCase) ? $"{fieldId}_id" : fieldId,
            DisplayName = fieldId.Replace('_', ' '),
            Type = type,
            References = type.Equals("relation", StringComparison.OrdinalIgnoreCase) ? "specification" : null,
            Options = type.Equals("enum", StringComparison.OrdinalIgnoreCase) ? ["low", "high"] : [],
            Computed = computed
                ? new ErpComputedField
                {
                    Op = "multiply",
                    Left = new ErpComputedOperand { Field = $"{fieldId}_base" },
                    Right = new ErpComputedOperand { Value = 1 }
                }
                : null
        });
    }
}

internal static class RelatedRecordActionGuardTestBundle
{
    public const string ActionKey = "release_batch";
    public const string QueryStepId = "find_approved_formula_revision";
    public const string GuardStepId = "require_approved_formula";

    public static ErpBundle Create(ErpActionFilter? identityFilter = null) => new()
    {
        Id = "related_record_guard_bundle",
        Name = "Related record guard bundle",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "manufacturing",
                Name = "Manufacturing",
                Entities =
                [
                    Entity(
                        "formula_revision",
                        "formula_revisions",
                        [
                            Field("name", "string", required: true),
                            EnumField("status", "draft", "approved", "revoked"),
                            Field("owner_principal", "integer", required: true)
                        ]),
                    Entity(
                        "specification",
                        "specifications",
                        [Field("name", "string", required: true)]),
                    Entity(
                        "batch",
                        "batches",
                        [
                            Field("name", "string", required: true),
                            Relation("formula_revision", "formula_revision_id", "formula_revision", required: true),
                            Relation("specification", "specification_id", "specification"),
                            EnumField("status", "draft", "released")
                        ])
                ]
            }
        ],
        Actions =
        [
            new ErpAction
            {
                Key = ActionKey,
                Label = "Release batch",
                Entity = "batch",
                Type = "record_action",
                Requires = new ErpActionRequires
                {
                    Permissions =
                    [
                        new ErpGrant { Action = "read", Entity = "batch" },
                        new ErpGrant { Action = "update", Entity = "batch" },
                        new ErpGrant { Action = "read", Entity = "formula_revision" }
                    ]
                },
                Steps =
                [
                    new ErpActionStep
                    {
                        Id = QueryStepId,
                        Type = "query_records",
                        Entity = "formula_revision",
                        Where =
                        [
                            identityFilter ?? IdentityFilter("eq", "formula_revision"),
                            new ErpActionFilter { Field = "status", Op = "eq", Value = "approved" }
                        ]
                    },
                    new ErpActionStep
                    {
                        Id = GuardStepId,
                        Type = "guard",
                        Condition = new ErpActionCondition
                        {
                            Op = "eq",
                            Left = new ErpActionOperand { ResultCount = QueryStepId },
                            Right = new ErpActionOperand { Value = 1 }
                        },
                        Error = "The linked formula revision must be approved."
                    },
                    new ErpActionStep
                    {
                        Id = "mark_batch_released",
                        Type = "update_current_record",
                        Set = new Dictionary<string, ErpActionValue>
                        {
                            ["status"] = new() { Value = "released" }
                        }
                    }
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["operator"] = Role(
                EntityGrant("read", "batch"),
                EntityGrant("update", "batch"),
                EntityGrant("read", "formula_revision"),
                ActionGrant()),
            ["no_formula_read"] = Role(
                EntityGrant("read", "batch"),
                EntityGrant("update", "batch"),
                ActionGrant()),
            ["field_blind"] = Role(
                EntityGrant("read", "batch"),
                EntityGrant("update", "batch"),
                EntityGrant("read", "formula_revision"),
                new ErpGrant
                {
                    Scope = PermissionGrantScopes.Field,
                    Action = "read",
                    Entity = "formula_revision",
                    Target = "name"
                },
                ActionGrant()),
            ["scoped_operator"] = Role(
                EntityGrant("read", "batch"),
                EntityGrant("update", "batch"),
                new ErpGrant
                {
                    Scope = PermissionGrantScopes.Record,
                    Action = "read",
                    Entity = "formula_revision",
                    RecordScope = new ErpRecordScope
                    {
                        Field = "owner_principal",
                        Source = "actor_principal"
                    }
                },
                ActionGrant())
        }
    };

    public static ErpActionFilter IdentityFilter(string op, string recordField) => new()
    {
        Field = "id",
        Op = op,
        ValueFrom = new ErpActionValueFrom { Record = recordField }
    };

    private static ErpRole Role(params ErpGrant[] grants) => new()
    {
        DisplayName = "Test role",
        Grants = grants.ToList()
    };

    private static ErpGrant EntityGrant(string action, string entity) => new()
    {
        Action = action,
        Entity = entity
    };

    private static ErpGrant ActionGrant() => new()
    {
        Scope = PermissionGrantScopes.Action,
        Action = "execute",
        Entity = "batch",
        Target = ActionKey
    };

    private static ErpEntity Entity(string id, string name, List<ErpField> fields) => new()
    {
        Id = id,
        Name = name,
        DisplayName = id.Replace('_', ' '),
        Fields = fields
    };

    private static ErpField Field(string id, string type, bool required = false) => new()
    {
        Id = id,
        Name = id,
        DisplayName = id.Replace('_', ' '),
        Type = type,
        Required = required
    };

    private static ErpField Relation(
        string id,
        string name,
        string references,
        bool required = false) => new()
    {
        Id = id,
        Name = name,
        DisplayName = id.Replace('_', ' '),
        Type = "relation",
        References = references,
        Required = required
    };

    private static ErpField EnumField(string id, params string[] options) => new()
    {
        Id = id,
        Name = id,
        DisplayName = id.Replace('_', ' '),
        Type = "enum",
        Required = true,
        Options = options.ToList()
    };
}
