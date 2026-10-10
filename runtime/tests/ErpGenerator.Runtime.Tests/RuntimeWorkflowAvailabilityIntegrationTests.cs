using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeWorkflowAvailabilityIntegrationTests
{
    [PostgresFact]
    public async Task Availability_includes_rejection_only_nested_and_scoped_legacy_lifecycle_transitions()
    {
        var adminConnectionString = TestPostgres.GetRequiredAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"erp_tenant_workflow_availability_{suffix}";
        await TestPostgres.CreateDatabaseAsync(adminConnectionString, database);

        try
        {
            var tenant = RuntimeTestTenant.Create(
                "workflow-availability",
                "workflow-availability.erpgenerator.example",
                database,
                TenantEnvironmentKind.Staging);
            var factory = TestPostgres.CreateConnectionFactory(adminConnectionString);
            var bundle = Bundle();
            var compiler = new ErpBundleCompiler();
            compiler.Validate(bundle).Should().BeEmpty();
            await new RuntimeDatabase(factory, new RuntimeSqlBuilder()).ApplyCompiledBundleAsync(
                tenant,
                compiler.Compile(bundle));

            var records = new RuntimeRecordStore(factory);
            var workflows = new RuntimeWorkflowStore(factory);
            var owner = new RuntimeUserContext(1, "owner@workflow-availability.test", "owner")
            {
                Roles = ["owner"]
            };
            var record = await records.CreateRecordAsync(
                tenant,
                owner,
                "requests",
                RuntimeJson.JsonObject(
                    ("name", "Availability proof"),
                    ("approval_status", "draft")));
            var recordId = Convert.ToInt64(record["id"]);

            (await ActionKeysAsync(workflows, tenant, owner, recordId)).Should().BeEquivalentTo(
                "close_business_state",
                "close_nested_state",
                "record_note",
                "reject_once",
                "submit_legacy_approval");

            await workflows.StartActionAsync(
                tenant,
                owner,
                "requests",
                recordId,
                "close_business_state");

            var afterTopLevelTransition = await ActionKeysAsync(workflows, tenant, owner, recordId);
            afterTopLevelTransition.Should().NotContain("close_business_state");
            afterTopLevelTransition.Should().Contain(
                ["close_nested_state", "record_note", "reject_once", "submit_legacy_approval"],
                "availability must be evaluated per workflow instead of globally from one lifecycle field");
            await FluentActions.Awaiting(() => workflows.StartActionAsync(
                    tenant,
                    owner,
                    "requests",
                    recordId,
                    "close_business_state"))
                .Should().ThrowAsync<RuntimeWorkflowActionNotAvailableException>();

            await workflows.StartActionAsync(
                tenant,
                owner,
                "requests",
                recordId,
                "close_nested_state");

            var afterNestedTransition = await ActionKeysAsync(workflows, tenant, owner, recordId);
            afterNestedTransition.Should().NotContain("close_nested_state",
                "nested update_record steps must participate in lifecycle repeat prevention");
            afterNestedTransition.Should().Contain(["record_note", "reject_once", "submit_legacy_approval"]);

            await workflows.StartActionAsync(
                tenant,
                owner,
                "requests",
                recordId,
                "submit_legacy_approval");

            var afterLegacyTransition = await ActionKeysAsync(workflows, tenant, owner, recordId);
            afterLegacyTransition.Should().NotContain("submit_legacy_approval",
                "legacy approval_status workflows must retain draft-only repeat prevention");
            afterLegacyTransition.Should().BeEquivalentTo(["record_note", "reject_once"],
                "an unrelated workflow with no transitioned lifecycle field must not inherit the legacy gate");
            await FluentActions.Awaiting(() => workflows.StartActionAsync(
                    tenant,
                    owner,
                    "requests",
                    recordId,
                    "submit_legacy_approval"))
                .Should().ThrowAsync<RuntimeWorkflowActionNotAvailableException>();

            var rejectionStart = await workflows.StartActionAsync(
                tenant,
                owner,
                "requests",
                recordId,
                "reject_once");
            rejectionStart.Task.Should().NotBeNull();
            (await ActionKeysAsync(workflows, tenant, owner, recordId))
                .Should().ContainSingle().Which.Should().Be("record_note",
                    "an active rejection workflow remains independently unavailable");

            var rejected = await workflows.RejectTaskAsync(
                tenant,
                owner,
                rejectionStart.Task!.Id,
                "terminal rejection proof");
            rejected.Instance.Status.Should().Be("rejected");
            (await ActionKeysAsync(workflows, tenant, owner, recordId))
                .Should().ContainSingle().Which.Should().Be("record_note",
                    "a lifecycle transition declared only in on_rejected must prevent repeat starts");
            await FluentActions.Awaiting(() => workflows.StartActionAsync(
                    tenant,
                    owner,
                    "requests",
                    recordId,
                    "reject_once"))
                .Should().ThrowAsync<RuntimeWorkflowActionNotAvailableException>();

            await workflows.StartActionAsync(
                tenant,
                owner,
                "requests",
                recordId,
                "record_note");
            (await ActionKeysAsync(workflows, tenant, owner, recordId))
                .Should().ContainSingle().Which.Should().Be("record_note");

            var final = await records.GetRecordAsync(tenant, "requests", recordId);
            final!["business_state"].Should().Be("closed");
            final["nested_state"].Should().Be("closed");
            final["rejection_state"].Should().Be("closed");
            final["approval_status"].Should().Be("submitted");
            final["note"].Should().Be("observed");
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(adminConnectionString, database);
        }
    }

    private static async Task<string[]> ActionKeysAsync(
        RuntimeWorkflowStore workflows,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        long recordId) =>
        (await workflows.ListRecordActionsAsync(tenant, user, "requests", recordId))
        .Select(action => action.Key)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static ErpBundle Bundle() => new()
    {
        Id = "workflow_availability",
        Name = "Workflow availability",
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
                        Id = "request",
                        Name = "requests",
                        DisplayName = "Requests",
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "name", Name = "name", DisplayName = "Name",
                                Type = "string", Required = true
                            },
                            new ErpField
                            {
                                Id = "note", Name = "note", DisplayName = "Note",
                                Type = "string", Required = false
                            },
                            LifecycleField("business_state", "Business state"),
                            LifecycleField("nested_state", "Nested state"),
                            LifecycleField("rejection_state", "Rejection state"),
                            new ErpField
                            {
                                Id = "approval_status", Name = "approval_status", DisplayName = "Approval status",
                                Type = "enum", Required = true, Options = ["draft", "submitted"]
                            }
                        ]
                    }
                ],
                Workflows =
                [
                    UpdateWorkflow(
                        "close_business",
                        "close_business_state",
                        new ErpWorkflowStep
                        {
                            Id = "mark_business_closed",
                            Type = "update_record",
                            Set = new Dictionary<string, string> { ["business_state"] = "closed" }
                        }),
                    UpdateWorkflow(
                        "close_nested",
                        "close_nested_state",
                        new ErpWorkflowStep
                        {
                            Id = "when_named",
                            Type = "condition",
                            Condition = new ErpWorkflowCondition
                            {
                                Op = "exists",
                                Left = new ErpWorkflowConditionOperand { Field = "name" }
                            },
                            Then =
                            [
                                new ErpWorkflowStep
                                {
                                    Id = "mark_nested_closed",
                                    Type = "update_record",
                                    Set = new Dictionary<string, string> { ["nested_state"] = "closed" }
                                }
                            ]
                        }),
                    UpdateWorkflow(
                        "ordinary_note",
                        "record_note",
                        new ErpWorkflowStep
                        {
                            Id = "set_note",
                            Type = "update_record",
                            Set = new Dictionary<string, string> { ["note"] = "observed" }
                        }),
                    new ErpWorkflow
                    {
                        Id = "rejection_only",
                        Name = "Rejection only",
                        Entity = "request",
                        Trigger = new ErpWorkflowTrigger { Action = "reject_once" },
                        Steps =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "owner_review",
                                Type = "approval",
                                AssignedTo = new ErpWorkflowAssignment
                                {
                                    Type = "role",
                                    Role = "owner"
                                },
                                RejectLabel = "Reject"
                            }
                        ],
                        OnRejected =
                        [
                            new ErpWorkflowStep
                            {
                                Id = "close_rejection_state",
                                Type = "update_record",
                                Set = new Dictionary<string, string> { ["rejection_state"] = "closed" }
                            }
                        ]
                    },
                    UpdateWorkflow(
                        "legacy_approval",
                        "submit_legacy_approval",
                        new ErpWorkflowStep
                        {
                            Id = "mark_submitted",
                            Type = "update_record",
                            Set = new Dictionary<string, string> { ["approval_status"] = "submitted" }
                        })
                ]
            }
        ],
        Roles = new Dictionary<string, ErpRole>
        {
            ["owner"] = new()
            {
                DisplayName = "Owner",
                Grants =
                [
                    new ErpGrant { Action = "*", Entity = "*" },
                    new ErpGrant { Action = "read", Entity = "request" },
                    new ErpGrant { Action = "read_task", Entity = "request" },
                    new ErpGrant { Action = "approve_task", Entity = "request" },
                    new ErpGrant { Action = "reject_task", Entity = "request" }
                ]
            }
        }
    };

    private static ErpField LifecycleField(string id, string label) => new()
    {
        Id = id,
        Name = id,
        DisplayName = label,
        Type = "enum",
        Required = true,
        Options = ["open", "closed"],
        Lifecycle = new ErpFieldLifecycle { Initial = "open", Transitions = "direct" }
    };

    private static ErpWorkflow UpdateWorkflow(
        string id,
        string action,
        ErpWorkflowStep step) => new()
    {
        Id = id,
        Name = id.Replace('_', ' '),
        Entity = "request",
        Trigger = new ErpWorkflowTrigger { Action = action },
        Steps = [step]
    };
}
