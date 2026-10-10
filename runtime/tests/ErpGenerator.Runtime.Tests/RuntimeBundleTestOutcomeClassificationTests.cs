using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeBundleTestOutcomeClassificationTests
{
    [Fact]
    public void Unexpected_success_and_wrong_typed_negative_fail_exact_matching()
    {
        var step = RunActionStep(
            BundleAcceptanceExpectedOutcomes.ActionConflict,
            BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable,
            "close_job");

        var unexpectedSuccess = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestStepActualOutcome.Success);
        var wrongNegative = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeActionGuardRejectedException(
                    "close_job",
                    "job_must_be_open",
                    "Action is not available for the current record state.")));

        unexpectedSuccess.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Failed);
        unexpectedSuccess.ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.Success);
        wrongNegative.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Failed);
        wrongNegative.ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.BusinessRuleRejected);
        wrongNegative.ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.ActionGuard);
    }

    [Fact]
    public void Same_message_from_a_different_exception_is_an_execution_error_not_action_conflict()
    {
        var step = RunActionStep(
            BundleAcceptanceExpectedOutcomes.ActionConflict,
            BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable,
            "close_job");
        const string message = "Action is not available for the current record state.";

        var typed = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeActionNotAvailableException("close_job")));
        var generic = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeActionValidationException(message)));

        typed.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        typed.ActualTarget.Should().Be("close_job");
        generic.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.ExecutionError);
        generic.ActualOutcome.Should().Be(RuntimeBundleTestActualOutcomes.ExecutionError);
        generic.ActualCode.Should().BeEmpty();
        generic.Detail.Should().Contain(message);
    }

    [Fact]
    public void Workflow_start_conflict_maps_to_the_exact_trigger_only_for_workflow_start_steps()
    {
        var step = new BundleAcceptanceStep
        {
            Id = "duplicate_approval_start",
            Type = "start_workflow_action",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            Action = "submit_for_approval",
            AsRole = "operator",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.ActionConflict,
                Code = BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable,
                Target = "submit_for_approval"
            }
        };

        var conflict = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeWorkflowActionNotAvailableException(
                    "submit_for_approval",
                    "A workflow instance is already active for this record.")));
        var unrelatedAction = RunActionStep(
            BundleAcceptanceExpectedOutcomes.ActionConflict,
            BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable,
            "close_job");
        var wrongStepType = RuntimeBundleTestOutcomeClassifier.Observe(
            unrelatedAction,
            RuntimeBundleTestOutcomeClassifier.FromException(
                unrelatedAction,
                new RuntimeWorkflowActionNotAvailableException(
                    "submit_for_approval",
                    "A workflow instance is already active for this record.")));
        var assignmentResolutionFailure = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeWorkflowConflictException(
                    "Workflow approval could not resolve one exact active relation user.")));

        conflict.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        conflict.ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.ActionConflict);
        conflict.ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable);
        conflict.ActualTarget.Should().Be("submit_for_approval");
        wrongStepType.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.ExecutionError);
        assignmentResolutionFailure.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.ExecutionError);
        assignmentResolutionFailure.ActualOutcome.Should().Be(RuntimeBundleTestActualOutcomes.ExecutionError);
    }

    [Fact]
    public void Only_a_genuinely_typed_action_permission_denial_can_match_permission_denied()
    {
        var step = RunActionStep(
            BundleAcceptanceExpectedOutcomes.PermissionDenied,
            BundleAcceptanceExpectedOutcomeCodes.PermissionNamedActionExecute,
            "close_job");
        const string message = "Action permission denied.";

        var typed = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeActionPermissionException(
                    "named_action.execute",
                    "close_job",
                    message)));
        var unclassified = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeActionPermissionException(message)));
        var malformedDefinition = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeActionDefinitionException("close_job", message)));

        typed.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        unclassified.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.ExecutionError);
        malformedDefinition.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.ExecutionError);
    }

    [Fact]
    public void Hidden_guard_field_denial_cannot_masquerade_as_entity_read_denial()
    {
        var step = RunActionStep(
            BundleAcceptanceExpectedOutcomes.PermissionDenied,
            BundleAcceptanceExpectedOutcomeCodes.PermissionEntityRead,
            "job");

        var observation = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeActionPermissionException(
                    "field.read",
                    "job.status",
                    "Action record access denied.")));

        observation.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.ExecutionError);
        observation.ActualOutcome.Should().Be(RuntimeBundleTestActualOutcomes.ExecutionError);
        observation.ActualCode.Should().BeEmpty();
    }

    [Fact]
    public void Guard_and_workflow_rejection_require_exact_typed_targets()
    {
        var guardStep = RunActionStep(
            BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
            BundleAcceptanceExpectedOutcomeCodes.ActionGuard,
            "job_must_be_open");
        var guard = RuntimeBundleTestOutcomeClassifier.Observe(
            guardStep,
            RuntimeBundleTestOutcomeClassifier.FromException(
                guardStep,
                new RuntimeActionGuardRejectedException(
                    "close_job",
                    "job_must_be_open",
                    "Job must be open.")));
        var workflowStep = new BundleAcceptanceStep
        {
            Id = "reject_manager_task",
            Type = "reject_task",
            Entity = "job",
            AsRole = "manager",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.WorkflowRejected,
                Code = BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected,
                Target = "manager_approval"
            }
        };
        var workflow = RuntimeBundleTestOutcomeClassifier.Observe(
            workflowStep,
            RuntimeBundleTestOutcomeClassifier.FromWorkflowRejectionResult(
                workflowStep,
                "rejected",
                "rejected",
                "manager_approval"));
        var wrongWorkflowTarget = RuntimeBundleTestOutcomeClassifier.Observe(
            workflowStep,
            RuntimeBundleTestStepActualOutcome.WorkflowTaskRejected("finance_approval"));
        var incompleteWorkflowRejection = RuntimeBundleTestOutcomeClassifier.Observe(
            workflowStep,
            RuntimeBundleTestOutcomeClassifier.FromWorkflowRejectionResult(
                workflowStep,
                "rejected",
                "waiting",
                "manager_approval"));

        guard.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        workflow.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        wrongWorkflowTarget.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Failed);
        incompleteWorkflowRejection.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Failed);
    }

    [Fact]
    public void Legacy_reject_task_success_remains_operation_completed_semantics()
    {
        var legacyStep = new BundleAcceptanceStep
        {
            Id = "legacy_reject",
            Type = "reject_task",
            Entity = "job",
            AsRole = "manager"
        };

        var actual = RuntimeBundleTestOutcomeClassifier.FromWorkflowRejectionResult(
            legacyStep,
            "rejected",
            "rejected",
            "manager_approval");
        var observation = RuntimeBundleTestOutcomeClassifier.Observe(legacyStep, actual);

        observation.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        observation.ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.Success);
    }

    [Fact]
    public void Unimplemented_record_scoped_permission_expectations_fail_closed_as_unsupported()
    {
        var step = new BundleAcceptanceStep
        {
            Id = "archive_assigned_job",
            Type = "run_action",
            Entity = "job",
            Action = "archive_job",
            AsRole = "technician",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                Code = BundleAcceptanceExpectedOutcomeCodes.PermissionRecordArchive,
                Target = "job"
            }
        };

        RuntimeBundleTestOutcomeClassifier.TryFindUnsupportedExpectedOutcome(step, out var actual)
            .Should().BeTrue();
        var observation = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            actual,
            RuntimeBundleTestStepObservationStatuses.Unsupported);

        observation.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Unsupported);
        observation.ActualOutcome.Should().Be(RuntimeBundleTestActualOutcomes.ExecutionError);
        observation.ActualCode.Should().Be(RuntimeBundleTestActualOutcomeCodes.UnsupportedExpectedOutcome);
    }

    [Fact]
    public void Exact_record_update_permission_denial_is_supported_and_classified()
    {
        var step = new BundleAcceptanceStep
        {
            Id = "update_assigned_job",
            Type = "update_record",
            Entity = "job",
            AsRole = "technician",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                Code = BundleAcceptanceExpectedOutcomeCodes.PermissionRecordUpdate,
                Target = "job"
            }
        };

        RuntimeBundleTestOutcomeClassifier.TryFindUnsupportedExpectedOutcome(step, out _)
            .Should().BeFalse();
        var observation = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.RecordUpdatePermissionDenied(step));

        observation.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        observation.ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.PermissionDenied);
        observation.ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.PermissionRecordUpdate);
        observation.ActualTarget.Should().Be("job");
    }

    [Fact]
    public void Exact_workflow_task_assignment_denial_requires_the_typed_assignment_exception_and_target()
    {
        var step = WorkflowDecisionStep(
            "approve_task",
            BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
            "manager_approval");

        BundleAcceptanceExpectedOutcomeContract.FindError(step).Should().BeNull();
        RuntimeBundleTestOutcomeClassifier.TryFindUnsupportedExpectedOutcome(step, out _).Should().BeFalse();

        var exact = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeWorkflowTaskAssignmentPermissionException(
                    "manager_approval",
                    "Workflow task is not assigned to this user.")));
        var wrongTarget = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeWorkflowTaskAssignmentPermissionException(
                    "finance_approval",
                    "Workflow task is not assigned to this user.")));
        var generic = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeWorkflowPermissionException("Workflow task is not assigned to this user.")));

        exact.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        exact.ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment);
        exact.ActualTarget.Should().Be("manager_approval");
        wrongTarget.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Failed);
        generic.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.ExecutionError);
    }

    [Theory]
    [InlineData("approve_task", BundleAcceptanceExpectedOutcomeCodes.PermissionRecordApproveTask)]
    [InlineData("reject_task", BundleAcceptanceExpectedOutcomeCodes.PermissionRecordRejectTask)]
    [InlineData("approve_task", BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment)]
    public void Legacy_v2_workflow_task_permission_denials_remain_unsupported(
        string stepType,
        string expectedCode)
    {
        var step = WorkflowDecisionStep(stepType, expectedCode, "manager_approval");

        RuntimeBundleTestOutcomeClassifier.TryFindUnsupportedExpectedOutcome(
                step,
                out var actual,
                supportsExactWorkflowTaskPermissionDenials: false)
            .Should().BeTrue();
        var observation = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            actual,
            RuntimeBundleTestStepObservationStatuses.Unsupported);

        observation.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Unsupported);
        observation.ActualOutcome.Should().Be(RuntimeBundleTestActualOutcomes.ExecutionError);
        observation.ActualCode.Should().Be(RuntimeBundleTestActualOutcomeCodes.UnsupportedExpectedOutcome);
    }

    [Theory]
    [InlineData("approve_task", BundleAcceptanceExpectedOutcomeCodes.PermissionRecordApproveTask)]
    [InlineData("reject_task", BundleAcceptanceExpectedOutcomeCodes.PermissionRecordRejectTask)]
    public void Exact_workflow_task_record_permission_denials_are_supported_and_action_specific(
        string stepType,
        string expectedCode)
    {
        var step = WorkflowDecisionStep(stepType, expectedCode, "manager_approval");
        var action = stepType;

        BundleAcceptanceExpectedOutcomeContract.FindError(step).Should().BeNull();
        RuntimeBundleTestOutcomeClassifier.TryFindUnsupportedExpectedOutcome(step, out _).Should().BeFalse();
        var exact = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeWorkflowTaskDecisionPermissionException(
                    action,
                    "manager_approval",
                    "Workflow task is not allowed for this record.")));
        var wrongAction = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeWorkflowTaskDecisionPermissionException(
                    stepType == "approve_task" ? "reject_task" : "approve_task",
                    "manager_approval",
                    "Workflow task is not allowed for this record.")));

        exact.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        exact.ActualCode.Should().Be(expectedCode);
        wrongAction.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.ExecutionError);
    }

    [Fact]
    public void Failed_step_id_collision_with_seed_ref_is_not_successful_record_provenance()
    {
        var seedReference = new RuntimeSeedRecordRef(
            "denied_create_job",
            "job",
            "job",
            42);
        var successfulProducedRecords = new Dictionary<string, RuntimeSeedRecordRef>(StringComparer.Ordinal);

        RuntimeBundleTestRunner.IsExactSuccessfulProducedRecordReference(
                successfulProducedRecords,
                seedReference,
                "job")
            .Should().BeFalse(
                "an attempted or denied step whose id collides with a seed ref is not a successful producer");

        successfulProducedRecords[seedReference.Ref] = seedReference;

        RuntimeBundleTestRunner.IsExactSuccessfulProducedRecordReference(
                successfulProducedRecords,
                seedReference,
                "job")
            .Should().BeTrue();
        RuntimeBundleTestRunner.IsExactSuccessfulProducedRecordReference(
                successfulProducedRecords,
                seedReference,
                "other_entity")
            .Should().BeFalse("producer provenance must include the exact canonical entity");
    }

    [Fact]
    public void Exact_record_create_permission_exception_is_supported_and_classified()
    {
        var step = new BundleAcceptanceStep
        {
            Id = "create_assigned_job",
            Type = "create_record",
            Entity = "job",
            AsRole = "technician",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                Code = BundleAcceptanceExpectedOutcomeCodes.PermissionRecordCreate,
                Target = "job"
            }
        };

        BundleAcceptanceExpectedOutcomeContract.FindError(step).Should().BeNull();
        RuntimeBundleTestOutcomeClassifier.TryFindUnsupportedExpectedOutcome(step, out _)
            .Should().BeFalse();
        var observation = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeRecordPermissionException("create", "jobs")));

        observation.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        observation.ActualOutcome.Should().Be(BundleAcceptanceExpectedOutcomes.PermissionDenied);
        observation.ActualCode.Should().Be(BundleAcceptanceExpectedOutcomeCodes.PermissionRecordCreate);
        observation.ActualTarget.Should().Be("job");
    }

    [Fact]
    public void Cancellation_is_never_converted_into_an_execution_observation()
    {
        var step = RunActionStep(
            BundleAcceptanceExpectedOutcomes.Success,
            string.Empty,
            string.Empty);

        var classify = () => RuntimeBundleTestOutcomeClassifier.FromException(
            step,
            new OperationCanceledException("cancelled"));

        classify.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Generic_workflow_read_denial_cannot_be_credited_as_record_start_denial()
    {
        var step = new BundleAcceptanceStep
        {
            Id = "denied_start",
            Type = "start_workflow_action",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            Action = "submit",
            AsRole = "scoped_operator",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                Code = BundleAcceptanceExpectedOutcomeCodes.PermissionRecordStartWorkflow,
                Target = "job"
            }
        };

        var observation = RuntimeBundleTestOutcomeClassifier.Observe(
            step,
            RuntimeBundleTestOutcomeClassifier.FromException(
                step,
                new RuntimeWorkflowPermissionException("Record read permission is required to start a workflow.")));

        observation.Status.Should().Be(RuntimeBundleTestStepObservationStatuses.ExecutionError);
        observation.ActualOutcome.Should().Be(RuntimeBundleTestActualOutcomes.ExecutionError);
        observation.ActualCode.Should().BeEmpty();
    }

    [Fact]
    public void Workflow_and_relation_guards_require_the_exact_typed_step_and_target()
    {
        var workflowStep = new BundleAcceptanceStep
        {
            Id = "activate_subscription",
            Type = "start_workflow_action",
            Entity = "subscription",
            Record = new BundleRecordSelector { Ref = "create_subscription" },
            Action = "activate",
            AsRole = "operator",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
                Code = BundleAcceptanceExpectedOutcomeCodes.WorkflowStartGuard,
                Target = "no_pending_tasks"
            }
        };
        var relationStep = new BundleAcceptanceStep
        {
            Id = "create_late_task",
            Type = "create_record",
            Entity = "onboarding_task",
            AsRole = "operator",
            Values = new Dictionary<string, object?> { ["subscription"] = 42 },
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
                Code = BundleAcceptanceExpectedOutcomeCodes.RelationCreateGuard,
                Target = "subscription"
            }
        };

        RuntimeBundleTestOutcomeClassifier.Observe(
                workflowStep,
                RuntimeBundleTestOutcomeClassifier.FromException(
                    workflowStep,
                    new RuntimeWorkflowStartGuardRejectedException("no_pending_tasks")))
            .Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);
        RuntimeBundleTestOutcomeClassifier.Observe(
                relationStep,
                RuntimeBundleTestOutcomeClassifier.FromException(
                    relationStep,
                    new RuntimeRelationCreateGuardRejectedException(
                        "onboarding_task",
                        "subscription",
                        "subscription",
                        "approval_status")))
            .Status.Should().Be(RuntimeBundleTestStepObservationStatuses.Passed);

        RuntimeBundleTestOutcomeClassifier.FromException(
                relationStep,
                new RuntimeWorkflowStartGuardRejectedException("no_pending_tasks"))
            .Outcome.Should().Be(RuntimeBundleTestActualOutcomes.ExecutionError);
        RuntimeBundleTestOutcomeClassifier.FromException(
                workflowStep,
                new RuntimeRelationCreateGuardRejectedException(
                    "onboarding_task",
                    "subscription",
                    "subscription",
                    "approval_status"))
            .Outcome.Should().Be(RuntimeBundleTestActualOutcomes.ExecutionError);
    }

    private static BundleAcceptanceStep RunActionStep(
        string outcome,
        string code,
        string target) =>
        new()
        {
            Id = "close_job_step",
            Type = "run_action",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "seed_job" },
            Action = "close_job",
            AsRole = "operator",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = outcome,
                Code = code,
                Target = target
            }
        };

    private static BundleAcceptanceStep WorkflowDecisionStep(
        string type,
        string code,
        string target) => new()
        {
            Id = $"{type}_manager_task",
            Type = type,
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            WorkflowRef = "start_job_approval",
            AsRole = "reviewer",
            Expected = new BundleAcceptanceExpectedOutcome
            {
                Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                Code = code,
                Target = target
            }
        };
}
