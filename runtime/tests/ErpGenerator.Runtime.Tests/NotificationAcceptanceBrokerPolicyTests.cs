using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Services.DeploymentBroker;

namespace ErpGenerator.Runtime.Tests;

public sealed class NotificationAcceptanceBrokerPolicyTests
{
    [Fact]
    public void Broker_accepts_exact_v3_notification_assertion()
    {
        var document = Contract();

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests([document]);

        validate.Should().NotThrow();
    }

    [Fact]
    public void Broker_rejects_notification_assertion_without_exact_checkpoint_binding()
    {
        var document = Contract(afterStep: BundleAcceptanceAssertionCheckpoints.Setup);

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests([document]);

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*notification_exists assertions require exact v3*");
    }

    [Fact]
    public void Broker_rejects_success_as_a_v3_reject_task_outcome()
    {
        var document = Contract();
        document.Steps.Add(new BundleAcceptanceStep
        {
            Id = "reject_job",
            Type = "reject_task",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            WorkflowRef = "start_job",
            AsActor = "requester",
            AsRole = "operator"
        });

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests([document]);

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*v3 reject_task*workflow_rejected*");
    }

    [Fact]
    public void Broker_compiled_binds_v3_rejection_to_an_exact_workflow_approval()
    {
        var valid = RejectionContract("manager_approval");
        var validateValid = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [valid],
            CompiledRejectionBundle());
        validateValid.Should().NotThrow();

        var invented = RejectionContract("invented_approval");
        var validateInvented = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [invented],
            CompiledRejectionBundle());
        validateInvented.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*invented_approval*exact approval step*");
    }

    [Theory]
    [InlineData("workflow_rejected", "workflow_status rejected")]
    [InlineData("status_before_rejection", "pre-rejection record_field_equals")]
    [InlineData("status_after_rejection", "post-rejection record_field_equals")]
    [InlineData("rejected_at_before_rejection", "pre-rejection record_field_equals")]
    [InlineData("rejected_at_after_rejection", "post-rejection record_field_equals")]
    [InlineData("rejection_audit", "audit_event_exists")]
    [InlineData("rejection_notification", "notification_exists")]
    public void Broker_compiled_requires_every_rejection_effect_assertion(
        string assertionId,
        string expectedReason)
    {
        var document = RejectionContract("manager_approval");
        document.Assertions.RemoveAll(assertion => assertion.Id == assertionId);

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            CompiledRejectionBundle());

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage($"*{expectedReason}*");
    }

    [Theory]
    [InlineData("rejected", "start_job")]
    [InlineData("draft", "create_job")]
    public void Broker_compiled_requires_a_runtime_distinct_immediate_pre_rejection_state(
        string preRejectionStatus,
        string preRejectionCheckpoint)
    {
        var document = RejectionContract(
            "manager_approval",
            preRejectionStatus,
            preRejectionCheckpoint);

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            CompiledRejectionBundle());

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*runtime-distinct value*immediately preceding checkpoint*");
    }

    [Fact]
    public void Broker_compiled_preserves_legacy_v2_reject_success_semantics()
    {
        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [LegacyRejectionContract()],
            CompiledRejectionBundle());

        validate.Should().NotThrow();
    }

    [Fact]
    public void Broker_compiled_requires_authored_step_correlation_for_a_reserved_rejection_event()
    {
        var document = RejectionContract("manager_approval");
        document.Assertions.RemoveAll(assertion => assertion.Id == "rejection_audit");
        document.Assertions.Add(RejectionAuditAssertion(
            "reserved_rejection_audit",
            "workflow.task.rejected",
            "authored_reserved_rejection"));
        var compiled = CompiledRejectionBundleWithAudits(
            AuditWorkflowStep("authored_reserved_rejection", "workflow.task.rejected"));

        var validateExact = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            compiled);
        validateExact.Should().NotThrow();

        document.Assertions.RemoveAll(assertion => assertion.Id == "reserved_rejection_audit");
        document.Assertions.Add(RejectionAuditAssertion(
            "reserved_rejection_audit",
            "workflow.task.rejected",
            "manager_approval"));

        var validateBuiltIn = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            compiled);
        validateBuiltIn.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*audit_event_exists*authored_reserved_rejection*");
    }

    [Fact]
    public void Broker_compiled_requires_one_exact_assertion_per_duplicate_rejection_audit_step()
    {
        var document = RejectionContract("manager_approval");
        document.Assertions.RemoveAll(assertion => assertion.Id == "rejection_audit");
        document.Assertions.Add(RejectionAuditAssertion(
            "first_duplicate_rejection_audit",
            "job.rejected",
            "first_rejection_audit"));
        document.Assertions.Add(RejectionAuditAssertion(
            "second_duplicate_rejection_audit",
            "job.rejected",
            "second_rejection_audit"));
        var compiled = CompiledRejectionBundleWithAudits(
            AuditWorkflowStep("first_rejection_audit", "job.rejected"),
            AuditWorkflowStep("second_rejection_audit", "job.rejected"));

        var validateExact = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            compiled);
        validateExact.Should().NotThrow();

        document.Assertions.RemoveAll(assertion => assertion.Id == "second_duplicate_rejection_audit");

        var validateCollapsed = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests(
            [document],
            compiled);
        validateCollapsed.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*audit_event_exists*second_rejection_audit*");
    }

    [Fact]
    public void Broker_requires_the_pending_target_at_the_immediate_rejection_checkpoint()
    {
        var document = RejectionContract("manager_approval");
        document.Assertions.RemoveAll(assertion => assertion.Id == "target_pending");

        var validate = () => RuntimeDeploymentBrokerService.ValidateAcceptanceTests([document]);

        validate.Should().Throw<RuntimeDeploymentBrokerValidationException>()
            .WithMessage("*pending task assertion*manager_approval*");
    }

    private static BundleAcceptanceTestDocument Contract(string afterStep = "start_job") => new()
    {
        Schema = BundleAcceptanceTestSchemas.V3,
        Test = new BundleAcceptanceTest { Key = "notification_broker", Label = "Notification broker" },
        Setup = new BundleAcceptanceSetup { Seed = "none", SeedHash = new string('a', 64) },
        Actors = [new BundleAcceptanceActor { Id = "requester", Roles = ["operator"] }],
        Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "create_job",
                Type = "create_record",
                Entity = "job",
                AsActor = "requester",
                AsRole = "operator",
                Values = new Dictionary<string, object?> { ["name"] = "Job" }
            },
            new BundleAcceptanceStep
            {
                Id = "start_job",
                Type = "start_workflow_action",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                Action = "submit",
                AsActor = "requester",
                AsRole = "operator"
            }
        ],
        Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Id = "notification_exists",
                Type = "notification_exists",
                AfterStep = afterStep,
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                WorkflowRef = "start_job",
                TaskStep = "manager_approval"
            }
        ]
    };

    private static BundleAcceptanceTestDocument RejectionContract(
        string target,
        string preRejectionStatus = "draft",
        string preRejectionCheckpoint = "start_job")
    {
        var document = Contract();
        document.Steps.Add(new BundleAcceptanceStep
        {
            Id = "reject_job",
            Type = "reject_task",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            WorkflowRef = "start_job",
            AsActor = "requester",
            AsRole = "operator",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.WorkflowRejected,
                Code = BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected,
                Target = target
            }
        });
        document.Assertions.Add(new BundleAcceptanceAssertion
        {
            Id = "target_pending",
            Type = "task_exists",
            AfterStep = "start_job",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            WorkflowRef = "start_job",
            TaskStep = target,
            Status = "pending"
        });
        document.Assertions.AddRange(
        [
            new BundleAcceptanceAssertion
            {
                Id = "workflow_rejected",
                Type = "workflow_status",
                AfterStep = "reject_job",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                Workflow = "job_approval",
                WorkflowRef = "start_job",
                Status = "rejected"
            },
            new BundleAcceptanceAssertion
            {
                Id = "status_before_rejection",
                Type = "record_field_equals",
                AfterStep = preRejectionCheckpoint,
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                Field = "status",
                Value = preRejectionStatus
            },
            new BundleAcceptanceAssertion
            {
                Id = "status_after_rejection",
                Type = "record_field_equals",
                AfterStep = "reject_job",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                Field = "status",
                Value = "rejected"
            },
            new BundleAcceptanceAssertion
            {
                Id = "rejected_at_before_rejection",
                Type = "record_field_equals",
                AfterStep = "start_job",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                Field = "rejected_at",
                Value = "2026-07-10T09:00:00Z"
            },
            new BundleAcceptanceAssertion
            {
                Id = "rejected_at_after_rejection",
                Type = "record_field_equals",
                AfterStep = "reject_job",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                Field = "rejected_at",
                Value = "2026-07-12T09:00:00Z"
            },
            RejectionAuditAssertion("rejection_audit", "job.rejected", "audit_rejection"),
            new BundleAcceptanceAssertion
            {
                Id = "rejection_notification",
                Type = "notification_exists",
                AfterStep = "reject_job",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                WorkflowRef = "start_job",
                TaskStep = "notify_requester"
            }
        ]);
        return document;
    }

    private static BundleAcceptanceTestDocument LegacyRejectionContract() => new()
    {
        Schema = BundleAcceptanceTestSchemas.V2,
        Test = new BundleAcceptanceTest { Key = "legacy_rejection", Label = "Legacy rejection" },
        Setup = new BundleAcceptanceSetup { Seed = "none" },
        Steps =
        [
            new BundleAcceptanceStep
            {
                Id = "create_job",
                Type = "create_record",
                Entity = "job",
                AsRole = "operator",
                Values = new Dictionary<string, object?> { ["name"] = "Job" }
            },
            new BundleAcceptanceStep
            {
                Id = "start_job",
                Type = "start_workflow_action",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                Action = "submit",
                AsRole = "operator"
            },
            new BundleAcceptanceStep
            {
                Id = "reject_job",
                Type = "reject_task",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" },
                WorkflowRef = "start_job",
                AsRole = "operator"
            }
        ],
        Assertions =
        [
            new BundleAcceptanceAssertion
            {
                Type = "record_exists",
                Entity = "job",
                Record = new BundleRecordSelector { Ref = "create_job" }
            }
        ]
    };

    private static CompiledBundle CompiledRejectionBundle() => new(
        "test_bundle",
        "Test Bundle",
        "1.0.0",
        new string('a', 64),
        [
            new EntityDefinition(
                "job",
                "ops",
                "job",
                "Job",
                "job",
                new EntityDisplayDefinition("name", null),
                [
                    new FieldDefinition(
                        "name",
                        "name",
                        "Name",
                        "string",
                        "name",
                        "text",
                        true,
                        false,
                        null,
                        [],
                        null),
                    new FieldDefinition(
                        "status",
                        "status",
                        "Status",
                        "enum",
                        "status",
                        "text",
                        true,
                        false,
                        null,
                        ["draft", "rejected"],
                        null),
                    new FieldDefinition(
                        "rejected_at",
                        "rejected_at",
                        "Rejected at",
                        "datetime",
                        "rejected_at",
                        "timestamptz",
                        false,
                        false,
                        null,
                        [],
                        null)
                ])
        ],
        [],
        [
            new WorkflowDefinition(
                "job_approval",
                "ops",
                "Job Approval",
                "job",
                [],
                new WorkflowTriggerDefinition("submit"),
                [
                    new WorkflowStepDefinition(
                        "manager_approval",
                        "approval",
                        "Manager approval",
                        null,
                        "Approve",
                        "Reject",
                        new Dictionary<string, string>(),
                        null,
                        [],
                        null,
                        null)
                ],
                [
                    new WorkflowStepDefinition(
                        "mark_rejected",
                        "update_record",
                        string.Empty,
                        null,
                        null,
                        null,
                        new Dictionary<string, string>
                        {
                            ["status"] = "rejected",
                            ["rejected_at"] = "2026-07-12T12:00:00+03:00"
                        },
                        null,
                        [],
                        null,
                        null),
                    new WorkflowStepDefinition(
                        "audit_rejection",
                        "audit_event",
                        string.Empty,
                        null,
                        null,
                        null,
                        new Dictionary<string, string>(),
                        null,
                        [],
                        "job.rejected",
                        "Rejected"),
                    new WorkflowStepDefinition(
                        "notify_requester",
                        "notification_placeholder",
                        string.Empty,
                        null,
                        null,
                        null,
                        new Dictionary<string, string>(),
                        null,
                        [],
                        null,
                        "Purchase request rejected")
                ])
        ],
        [],
        [new RoleDefinition("operator", "Operator", [])],
        [],
        new DashboardDefinition([]),
        [],
        []);

    private static BundleAcceptanceAssertion RejectionAuditAssertion(
        string id,
        string eventKey,
        string auditStep) => new()
        {
            Id = id,
            Type = "audit_event_exists",
            AfterStep = "reject_job",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            Event = eventKey,
            AuditStep = auditStep
        };

    private static WorkflowStepDefinition AuditWorkflowStep(string id, string eventKey) => new(
        id,
        "audit_event",
        string.Empty,
        null,
        null,
        null,
        new Dictionary<string, string>(),
        null,
        [],
        eventKey,
        "Rejected");

    private static CompiledBundle CompiledRejectionBundleWithAudits(
        params WorkflowStepDefinition[] auditSteps)
    {
        var compiled = CompiledRejectionBundle();
        var workflow = compiled.Workflows.Single();
        var onRejected = workflow.OnRejected
            .Where(step => !step.Type.Equals("audit_event", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var notificationIndex = onRejected.FindIndex(step => step.Type.Equals(
            "notification_placeholder",
            StringComparison.OrdinalIgnoreCase));
        onRejected.InsertRange(notificationIndex < 0 ? onRejected.Count : notificationIndex, auditSteps);
        return compiled with
        {
            Workflows = [workflow with { OnRejected = onRejected }]
        };
    }
}
