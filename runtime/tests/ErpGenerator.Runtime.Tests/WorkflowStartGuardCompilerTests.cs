using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class WorkflowStartGuardCompilerTests
{
    [Fact]
    public void Exact_typed_guard_compiles_with_closed_metadata_and_fixed_error()
    {
        var compiled = new ErpBundleCompiler().Compile(Bundle());

        var guard = compiled.Workflows.Single(workflow => workflow.Id == "subscription_activation")
            .StartGuards.Should().ContainSingle().Which;
        guard.Id.Should().Be("no_pending_tasks");
        guard.Entity.Should().Be("onboarding_task");
        guard.ResultCountOp.Should().Be("eq");
        guard.ResultCount.Should().Be(0);
        guard.Error.Should().Be("Workflow start requirements are not satisfied.");
        guard.Error.Should().NotContain("customer-secret");
        guard.Where.Should().HaveCount(2);
        guard.Where[0].Field.Should().Be("subscription");
        guard.Where[0].Op.Should().Be("eq");
        guard.Where[0].ValueFrom!.Record.Should().Be("id");
        guard.Where[1].Field.Should().Be("status");
        guard.Where[1].Value.Should().Be("pending");
    }

    [Fact]
    public void Same_entity_guard_requires_explicit_id_to_current_id_correlation()
    {
        var exact = Bundle(
            guardEntity: "subscription",
            filters:
            [
                new ErpActionFilter
                {
                    Field = "id",
                    Op = "eq",
                    ValueFrom = new ErpActionValueFrom { Record = "id" }
                }
            ]);
        var missing = Bundle(
            guardEntity: "subscription",
            filters:
            [
                new ErpActionFilter { Field = "approval_status", Op = "eq", Value = "draft" }
            ]);

        new ErpBundleCompiler().Validate(exact).Should().BeEmpty();
        new ErpBundleCompiler().Validate(missing).Should().Contain(error =>
            error.Contains("must correlate", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("subscription_id", "id")]
    [InlineData("subscription", "subscription_id")]
    public void Aliased_target_or_source_fields_fail_closed(
        string targetField,
        string sourceField)
    {
        var bundle = Bundle(filters:
        [
            new ErpActionFilter
            {
                Field = targetField,
                Op = "eq",
                ValueFrom = new ErpActionValueFrom { Record = sourceField }
            },
            new ErpActionFilter { Field = "status", Op = "eq", Value = "pending" }
        ]);

        new ErpBundleCompiler().Validate(bundle).Should().Contain(error =>
            error.Contains("canonical field id", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("must resolve", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unbounded_or_unsatisfiable_result_count_contracts_are_rejected()
    {
        var aboveMaximum = Bundle(resultCount: ErpWorkflowStartGuardLimits.MaxResultCount + 1);
        var impossibleMaximum = Bundle(
            resultCountOp: "gt",
            resultCount: ErpWorkflowStartGuardLimits.MaxResultCount);

        new ErpBundleCompiler().Validate(aboveMaximum).Should().Contain(error =>
            error.Contains("between zero", StringComparison.OrdinalIgnoreCase));
        new ErpBundleCompiler().Validate(impossibleMaximum).Should().Contain(error =>
            error.Contains("bounded maximum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Maximum_length_workflow_id_does_not_overflow_internal_guard_validation_keys()
    {
        var workflowId = $"w{new string('x', 62)}";

        new ErpBundleCompiler().Validate(Bundle(workflowId: workflowId)).Should().BeEmpty();
    }

    [Fact]
    public void Mixed_literal_and_value_from_operand_is_rejected_at_compile_time()
    {
        var filters = ValidFilters();
        filters[1] = new ErpActionFilter
        {
            Field = "status",
            Op = "eq",
            Value = "pending",
            ValueFrom = new ErpActionValueFrom { Record = "approval_status" }
        };

        new ErpBundleCompiler().Validate(Bundle(filters: filters)).Should().Contain(error =>
            error.Contains("exactly one scalar value or value_from operand", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_value_from_operand_is_rejected_at_compile_time()
    {
        var filters = ValidFilters();
        filters[1] = new ErpActionFilter
        {
            Field = "status",
            Op = "eq",
            ValueFrom = new ErpActionValueFrom()
        };

        new ErpBundleCompiler().Validate(Bundle(filters: filters)).Should().Contain(error =>
            error.Contains("value_from must reference one exact current-record field", StringComparison.Ordinal));
    }

    [Fact]
    public void Scalar_operator_with_values_list_is_rejected_at_compile_time()
    {
        var filters = ValidFilters();
        filters[1] = new ErpActionFilter
        {
            Field = "status",
            Op = "eq",
            Value = "pending",
            Values = ["done"]
        };

        new ErpBundleCompiler().Validate(Bundle(filters: filters)).Should().Contain(error =>
            error.Contains("cannot declare values", StringComparison.Ordinal));
    }

    [Fact]
    public void Nonintegral_integer_literal_is_rejected_at_compile_time()
    {
        var filters = ValidFilters();
        filters.Add(new ErpActionFilter { Field = "sequence", Op = "eq", Value = 1.5m });

        new ErpBundleCompiler().Validate(Bundle(filters: filters)).Should().Contain(error =>
            error.Contains("literal for 'onboarding_task.sequence'", StringComparison.Ordinal) &&
            error.Contains("must exactly match the runtime field type", StringComparison.Ordinal));
    }

    [Fact]
    public void Noncanonical_enum_literal_is_rejected_at_compile_time()
    {
        var filters = ValidFilters();
        filters[1] = new ErpActionFilter { Field = "status", Op = "eq", Value = "Pending" };

        new ErpBundleCompiler().Validate(Bundle(filters: filters)).Should().Contain(error =>
            error.Contains("literal for 'onboarding_task.status'", StringComparison.Ordinal) &&
            error.Contains("canonical options", StringComparison.Ordinal));
    }

    [Fact]
    public void Exists_filter_operator_is_rejected_at_compile_time()
    {
        var filters = ValidFilters();
        filters[1] = new ErpActionFilter { Field = "status", Op = "exists" };

        new ErpBundleCompiler().Validate(Bundle(filters: filters)).Should().Contain(error =>
            error.Contains("unsupported filter operator 'exists'", StringComparison.Ordinal));
    }

    private static List<ErpActionFilter> ValidFilters() =>
    [
        new ErpActionFilter
        {
            Field = "subscription",
            Op = "eq",
            ValueFrom = new ErpActionValueFrom { Record = "id" }
        },
        new ErpActionFilter { Field = "status", Op = "eq", Value = "pending" }
    ];

    private static ErpBundle Bundle(
        string guardEntity = "onboarding_task",
        IReadOnlyList<ErpActionFilter>? filters = null,
        string resultCountOp = "eq",
        int resultCount = 0,
        string workflowId = "subscription_activation") => new()
    {
        Id = "workflow_start_guard_compiler",
        Name = "Workflow start guard compiler",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "subscriptions",
                Name = "Subscriptions",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "subscription",
                        Name = "subscriptions",
                        DisplayName = "Subscriptions",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "name", Name = "name", DisplayName = "Name",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "approval_status", Name = "approval_status", DisplayName = "Approval status",
                                Type = "enum", Required = true, Options = ["draft", "activated"]
                            }
                        ]
                    },
                    new ErpEntity
                    {
                        Id = "onboarding_task",
                        Name = "onboarding_tasks",
                        DisplayName = "Onboarding tasks",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "subscription", Name = "subscription_id", DisplayName = "Subscription",
                                Type = "relation", Required = true, References = "subscription"
                            },
                            new ErpField
                            {
                                Id = "status", Name = "status", DisplayName = "Status",
                                Type = "enum", Required = true, Options = ["pending", "done"]
                            },
                            new ErpField
                            {
                                Id = "sequence", Name = "sequence", DisplayName = "Sequence",
                                Type = "integer", Required = false
                            }
                        ]
                    }
                ],
                Workflows =
                [
                    new ErpWorkflow
                    {
                        Id = workflowId,
                        Name = "Subscription activation",
                        Entity = "subscription",
                        Trigger = new ErpWorkflowTrigger { Action = "activate" },
                        StartGuards =
                        [
                            new ErpWorkflowStartGuard
                            {
                                Id = "no_pending_tasks",
                                Entity = guardEntity,
                                Where = (filters ??
                                [
                                    new ErpActionFilter
                                    {
                                        Field = "subscription",
                                        Op = "eq",
                                        ValueFrom = new ErpActionValueFrom { Record = "id" }
                                    },
                                    new ErpActionFilter { Field = "status", Op = "eq", Value = "pending" }
                                ]).ToList(),
                                ResultCountOp = resultCountOp,
                                ResultCount = resultCount,
                                Error = "customer-secret: raw authored reason must never escape"
                            }
                        ],
                        Steps =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "mark_activated",
                                Type = "update_record",
                                Set = new Dictionary<string, string> { ["approval_status"] = "activated" }
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
