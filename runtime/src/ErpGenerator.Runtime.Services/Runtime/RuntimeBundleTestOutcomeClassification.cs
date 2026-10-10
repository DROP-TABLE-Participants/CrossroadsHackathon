using ErpGenerator.Runtime.Shared.Bundles;

namespace ErpGenerator.Runtime.Services.Runtime;

public static class RuntimeBundleTestStepObservationStatuses
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string ExecutionError = "execution_error";
    public const string Unsupported = "unsupported";
}

public static class RuntimeBundleTestAssertionObservationStatuses
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string ExecutionError = "execution_error";
}

public sealed record RuntimeBundleTestAssertionObservation(
    string AssertionId,
    string AssertionType,
    string AfterStep,
    string ContractItemHash,
    string Status,
    string Detail)
{
    public string Entity { get; init; } = string.Empty;

    public string RecordRef { get; init; } = string.Empty;

    public long? RecordId { get; init; }

    public string Field { get; init; } = string.Empty;

    public object? ExpectedValue { get; init; }

    public object? ActualValue { get; init; }

    public string ExpectedValueDigestSchema { get; init; } = string.Empty;

    public string ExpectedValueKind { get; init; } = string.Empty;

    public string ExpectedValueDigest { get; init; } = string.Empty;

    public string ActualValueDigestSchema { get; init; } = string.Empty;

    public string ActualValueKind { get; init; } = string.Empty;

    public string ActualValueDigest { get; init; } = string.Empty;

    public string WorkflowRef { get; init; } = string.Empty;

    public long? WorkflowInstanceId { get; init; }

    public string Workflow { get; init; } = string.Empty;

    public string WorkflowStatus { get; init; } = string.Empty;

    public IReadOnlyList<long> TaskIds { get; init; } = [];

    public IReadOnlyList<string> TaskSteps { get; init; } = [];

    public IReadOnlyList<string> TaskStatuses { get; init; } = [];

    public string ReportRef { get; init; } = string.Empty;

    public string Report { get; init; } = string.Empty;

    public string Metric { get; init; } = string.Empty;

    public string Widget { get; init; } = string.Empty;

    /// <summary>
    /// The actual report bound to the exact dashboard widget observed at run
    /// time. Empty when no unique widget key was found.
    /// </summary>
    public string DashboardWidgetReport { get; init; } = string.Empty;

    /// <summary>
    /// The actual metric bound to the exact dashboard widget observed at run
    /// time. Empty when no unique widget key was found.
    /// </summary>
    public string DashboardWidgetMetric { get; init; } = string.Empty;

    public int? RowCount { get; init; }

    public IReadOnlyList<object?> ReportMetricValues { get; init; } = [];

    public IReadOnlyList<BundleAcceptanceEvidenceDigest> ReportMetricDigests { get; init; } = [];

    /// <summary>
    /// Canonical SHA-256 of the bounded report shape, ordered output, applied
    /// limit, and truncation state. Raw rows are never retained.
    /// </summary>
    public string ReportOutputHash { get; init; } = string.Empty;

    public bool ReportTruncated { get; init; }

    public int? ReportAppliedLimit { get; init; }

    public IReadOnlyList<long> AuditEventIds { get; init; } = [];

    public string AuditStep { get; init; } = string.Empty;

    public IReadOnlyList<long> NotificationIds { get; init; } = [];

    /// <summary>
    /// Closed notification rows retained for exact qualification. The parallel
    /// <see cref="NotificationIds"/> list remains for compatibility and must
    /// contain the same identities in the same order.
    /// </summary>
    public IReadOnlyList<RuntimeBundleTestNotificationObservation> Notifications { get; init; } = [];

    public string ActorId { get; init; } = string.Empty;

    public long? PrincipalId { get; init; }
}

public sealed record RuntimeBundleTestNotificationObservation(
    long Id,
    long? TaskId,
    string NotificationType,
    string? RecipientRole,
    long? RecipientUserId);

public static class RuntimeBundleTestActualOutcomes
{
    public const string ExecutionError = "execution_error";
}

public static class RuntimeBundleTestActualOutcomeCodes
{
    public const string UnsupportedExpectedOutcome = "runtime.unsupported_expected_outcome";
}

public static class RuntimeBundleTestEvidenceFailureCodes
{
    public const string StepFailed = "acceptance.step_failed";
    public const string StepExecutionError = "acceptance.step_execution_error";
    public const string AssertionFailed = "acceptance.assertion_failed";
    public const string AssertionExecutionError = "acceptance.assertion_execution_error";
    public const string TestExecutionError = "acceptance.test_execution_error";
}

public sealed record RuntimeBundleTestStepObservation(
    string StepId,
    string StepType,
    string ExpectedOutcome,
    string ExpectedCode,
    string ExpectedTarget,
    string ActualOutcome,
    string ActualCode,
    string ActualTarget,
    string Status,
    string Detail)
{
    public string ContractItemHash { get; init; } = string.Empty;

    public string ActorId { get; init; } = string.Empty;

    public IReadOnlyList<string> ActorRoles { get; init; } = [];

    public long? PrincipalId { get; init; }

    public string Entity { get; init; } = string.Empty;

    public string RecordRef { get; init; } = string.Empty;

    public long? RecordId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string WorkflowRef { get; init; } = string.Empty;

    public long? WorkflowInstanceId { get; init; }

    public string Workflow { get; init; } = string.Empty;

    public long? TaskId { get; init; }

    public string WorkflowStep { get; init; } = string.Empty;

    /// <summary>
    /// Exact user assignment retained from the workflow task bound to this
    /// step. Null for role-assigned tasks and for steps without a task.
    /// </summary>
    public long? TaskAssignedUserId { get; init; }

    /// <summary>
    /// Exact role assignment retained from the workflow task bound to this
    /// step. Empty for user-assigned tasks and for steps without a task.
    /// </summary>
    public string TaskAssignedRole { get; init; } = string.Empty;

    public string Report { get; init; } = string.Empty;

    public string Schedule { get; init; } = string.Empty;

    public string ScheduleTime { get; init; } = string.Empty;

    public DateTimeOffset? ScheduleObservedAt { get; init; }

    public long? ScheduleJobId { get; init; }

    public string ScheduleJobStatus { get; init; } = string.Empty;

    public DateTimeOffset? ScheduleDueAt { get; init; }

    public int? ScheduleCycleClaimed { get; init; }

    public int? ScheduleCycleCompleted { get; init; }

    public string ProducedRecordRef { get; init; } = string.Empty;

    public string ProducedEntity { get; init; } = string.Empty;

    public long? ProducedRecordId { get; init; }
}

internal sealed record RuntimeBundleTestStepActualOutcome(
    string Outcome,
    string Code,
    string Target,
    string Detail)
{
    public static RuntimeBundleTestStepActualOutcome Success { get; } =
        new(BundleAcceptanceExpectedOutcomes.Success, string.Empty, string.Empty, string.Empty);

    public static RuntimeBundleTestStepActualOutcome WorkflowTaskRejected(string approvalStepId) =>
        new(
            BundleAcceptanceExpectedOutcomes.WorkflowRejected,
            BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected,
            approvalStepId,
            "Workflow task was rejected.");
}

internal static class RuntimeBundleTestOutcomeClassifier
{
    public static RuntimeBundleTestStepObservation Observe(
        BundleAcceptanceStep step,
        RuntimeBundleTestStepActualOutcome actual,
        string? statusOverride = null)
    {
        var expected = step.Expected ?? new BundleAcceptanceExpectedOutcome();
        var matched = string.Equals(expected.Outcome, actual.Outcome, StringComparison.Ordinal) &&
                      string.Equals(expected.Code, actual.Code, StringComparison.Ordinal) &&
                      string.Equals(expected.Target, actual.Target, StringComparison.Ordinal);
        var status = statusOverride ?? (matched
            ? RuntimeBundleTestStepObservationStatuses.Passed
            : string.Equals(actual.Outcome, RuntimeBundleTestActualOutcomes.ExecutionError, StringComparison.Ordinal)
                ? RuntimeBundleTestStepObservationStatuses.ExecutionError
                : RuntimeBundleTestStepObservationStatuses.Failed);
        var detail = matched
            ? actual.Detail
            : BuildMismatchDetail(step.Id, expected, actual);

        return new RuntimeBundleTestStepObservation(
            step.Id,
            step.Type,
            expected.Outcome,
            expected.Code,
            expected.Target,
            actual.Outcome,
            actual.Code,
            actual.Target,
            status,
            detail);
    }

    public static RuntimeBundleTestStepActualOutcome FromException(
        BundleAcceptanceStep step,
        Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            throw exception;
        }

        return exception switch
        {
            RuntimeLifecycleTransitionDeniedException denial => new RuntimeBundleTestStepActualOutcome(
                BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
                BundleAcceptanceExpectedOutcomeCodes.LifecycleNamedActionRequired,
                denial.Field,
                denial.Message),
            RuntimeBundleTestPermissionDeniedException denial => new RuntimeBundleTestStepActualOutcome(
                BundleAcceptanceExpectedOutcomes.PermissionDenied,
                denial.Code,
                denial.Target,
                denial.Message),
            RuntimeActionPermissionException denial when TryMapActionPermissionCode(
                denial.PermissionAction,
                out var code) => new RuntimeBundleTestStepActualOutcome(
                BundleAcceptanceExpectedOutcomes.PermissionDenied,
                code,
                denial.Target,
                denial.Message),
            RuntimeRecordPermissionException denial when
                denial.Action.Equals("create", StringComparison.OrdinalIgnoreCase) =>
                new RuntimeBundleTestStepActualOutcome(
                    BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    BundleAcceptanceExpectedOutcomeCodes.PermissionRecordCreate,
                    step.Entity,
                    denial.Message),
            RuntimeWorkflowTaskAssignmentPermissionException denial when
                IsWorkflowTaskDecisionStep(step.Type) => new RuntimeBundleTestStepActualOutcome(
                    BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment,
                    denial.WorkflowStep,
                    denial.Message),
            RuntimeWorkflowTaskDecisionPermissionException denial when
                WorkflowDecisionActionMatchesStep(denial.Action, step.Type) =>
                new RuntimeBundleTestStepActualOutcome(
                    BundleAcceptanceExpectedOutcomes.PermissionDenied,
                    step.Type.Equals("approve_task", StringComparison.OrdinalIgnoreCase)
                        ? BundleAcceptanceExpectedOutcomeCodes.PermissionRecordApproveTask
                        : BundleAcceptanceExpectedOutcomeCodes.PermissionRecordRejectTask,
                    denial.WorkflowStep,
                    denial.Message),
            RuntimeActionNotAvailableException unavailable => new RuntimeBundleTestStepActualOutcome(
                BundleAcceptanceExpectedOutcomes.ActionConflict,
                BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable,
                unavailable.ActionKey,
                unavailable.Message),
            RuntimeWorkflowActionNotAvailableException conflict when
                step.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase) =>
                new RuntimeBundleTestStepActualOutcome(
                    BundleAcceptanceExpectedOutcomes.ActionConflict,
                    BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable,
                    conflict.ActionKey,
                    conflict.Message),
            RuntimeActionGuardRejectedException guard => new RuntimeBundleTestStepActualOutcome(
                BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
                BundleAcceptanceExpectedOutcomeCodes.ActionGuard,
                guard.GuardStepId,
                guard.Message),
            RuntimeWorkflowStartGuardRejectedException guard when
                step.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase) =>
                new RuntimeBundleTestStepActualOutcome(
                    BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
                    BundleAcceptanceExpectedOutcomeCodes.WorkflowStartGuard,
                    guard.GuardId,
                    guard.Message),
            RuntimeRelationCreateGuardRejectedException guard when
                step.Type.Equals("create_record", StringComparison.OrdinalIgnoreCase) =>
                new RuntimeBundleTestStepActualOutcome(
                    BundleAcceptanceExpectedOutcomes.BusinessRuleRejected,
                    BundleAcceptanceExpectedOutcomeCodes.RelationCreateGuard,
                    guard.RelationField,
                    guard.Message),
            _ => new RuntimeBundleTestStepActualOutcome(
                RuntimeBundleTestActualOutcomes.ExecutionError,
                string.Empty,
                string.Empty,
                exception.Message)
        };
    }

    public static bool TryFindUnsupportedExpectedOutcome(
        BundleAcceptanceStep step,
        out RuntimeBundleTestStepActualOutcome unsupported,
        bool supportsExactWorkflowTaskPermissionDenials = true)
    {
        unsupported = null!;
        var expected = step.Expected ?? new BundleAcceptanceExpectedOutcome();
        if (string.Equals(expected.Outcome, BundleAcceptanceExpectedOutcomes.Success, StringComparison.Ordinal) ||
            IsSupportedNegative(step, expected.Code, supportsExactWorkflowTaskPermissionDenials))
        {
            return false;
        }

        unsupported = new RuntimeBundleTestStepActualOutcome(
            RuntimeBundleTestActualOutcomes.ExecutionError,
            RuntimeBundleTestActualOutcomeCodes.UnsupportedExpectedOutcome,
            expected.Target,
            $"Acceptance step '{step.Id}' expected code '{expected.Code}' is not executable by the current runtime permission primitives.");
        return true;
    }

    public static RuntimeBundleTestStepActualOutcome FromWorkflowRejectionResult(
        BundleAcceptanceStep step,
        string taskStatus,
        string instanceStatus,
        string approvalStepId)
    {
        var explicitlyExpectsRejection = string.Equals(
            step.Expected?.Outcome,
            BundleAcceptanceExpectedOutcomes.WorkflowRejected,
            StringComparison.Ordinal);
        return explicitlyExpectsRejection &&
               string.Equals(taskStatus, "rejected", StringComparison.Ordinal) &&
               string.Equals(instanceStatus, "rejected", StringComparison.Ordinal)
            ? RuntimeBundleTestStepActualOutcome.WorkflowTaskRejected(approvalStepId)
            : RuntimeBundleTestStepActualOutcome.Success;
    }

    public static RuntimeBundleTestStepActualOutcome RecordUpdatePermissionDenied(
        BundleAcceptanceStep step) =>
        new(
            BundleAcceptanceExpectedOutcomes.PermissionDenied,
            BundleAcceptanceExpectedOutcomeCodes.PermissionRecordUpdate,
            step.Entity,
            "The exact causally produced record is outside the actor's update scope.");

    private static bool IsSupportedNegative(
        BundleAcceptanceStep step,
        string code,
        bool supportsExactWorkflowTaskPermissionDenials)
    {
        var type = step.Type.Trim().ToLowerInvariant();
        return code switch
        {
            BundleAcceptanceExpectedOutcomeCodes.PermissionEntityCreate =>
                type is "create_record" or "run_action",
            BundleAcceptanceExpectedOutcomeCodes.PermissionRecordCreate =>
                type == "create_record",
            BundleAcceptanceExpectedOutcomeCodes.PermissionEntityRead => type == "run_action",
            BundleAcceptanceExpectedOutcomeCodes.PermissionEntityUpdate =>
                type is "update_record" or "run_action",
            BundleAcceptanceExpectedOutcomeCodes.PermissionRecordUpdate =>
                type == "update_record",
            BundleAcceptanceExpectedOutcomeCodes.PermissionFieldWrite =>
                type is "create_record" or "update_record",
            BundleAcceptanceExpectedOutcomeCodes.PermissionNamedActionExecute => type == "run_action",
            BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowStart =>
                type == "start_workflow_action",
            BundleAcceptanceExpectedOutcomeCodes.PermissionRecordStartWorkflow =>
                type == "start_workflow_action",
            BundleAcceptanceExpectedOutcomeCodes.PermissionRecordApproveTask =>
                supportsExactWorkflowTaskPermissionDenials && type == "approve_task",
            BundleAcceptanceExpectedOutcomeCodes.PermissionRecordRejectTask =>
                supportsExactWorkflowTaskPermissionDenials && type == "reject_task",
            BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment =>
                supportsExactWorkflowTaskPermissionDenials && type is ("approve_task" or "reject_task"),
            BundleAcceptanceExpectedOutcomeCodes.PermissionReportExecute => type == "call_report",
            BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable =>
                type is "run_action" or "start_workflow_action",
            BundleAcceptanceExpectedOutcomeCodes.ActionGuard => type == "run_action",
            BundleAcceptanceExpectedOutcomeCodes.WorkflowStartGuard => type == "start_workflow_action",
            BundleAcceptanceExpectedOutcomeCodes.RelationCreateGuard => type == "create_record",
            BundleAcceptanceExpectedOutcomeCodes.LifecycleNamedActionRequired => type == "update_record",
            BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected => type == "reject_task",
            _ => false
        };
    }

    private static bool TryMapActionPermissionCode(string permissionAction, out string code)
    {
        code = permissionAction.Trim().ToLowerInvariant() switch
        {
            "named_action.execute" => BundleAcceptanceExpectedOutcomeCodes.PermissionNamedActionExecute,
            "create" => BundleAcceptanceExpectedOutcomeCodes.PermissionEntityCreate,
            "read" => BundleAcceptanceExpectedOutcomeCodes.PermissionEntityRead,
            "update" => BundleAcceptanceExpectedOutcomeCodes.PermissionEntityUpdate,
            "archive" => BundleAcceptanceExpectedOutcomeCodes.PermissionEntityArchive,
            "restore" => BundleAcceptanceExpectedOutcomeCodes.PermissionEntityRestore,
            _ => string.Empty
        };
        return code.Length > 0;
    }

    private static bool IsWorkflowTaskDecisionStep(string? stepType) =>
        stepType?.Trim().ToLowerInvariant() is "approve_task" or "reject_task";

    private static bool WorkflowDecisionActionMatchesStep(string? action, string? stepType) =>
        action?.Trim().ToLowerInvariant() switch
        {
            "approve_task" => stepType?.Trim().Equals("approve_task", StringComparison.OrdinalIgnoreCase) == true,
            "reject_task" => stepType?.Trim().Equals("reject_task", StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };

    private static string BuildMismatchDetail(
        string stepId,
        BundleAcceptanceExpectedOutcome expected,
        RuntimeBundleTestStepActualOutcome actual)
    {
        var summary =
            $"Acceptance step '{stepId}' expected outcome '{expected.Outcome}' code '{expected.Code}' target '{expected.Target}' " +
            $"but observed outcome '{actual.Outcome}' code '{actual.Code}' target '{actual.Target}'.";
        return string.IsNullOrWhiteSpace(actual.Detail)
            ? summary
            : $"{summary} {actual.Detail}";
    }
}

internal sealed class RuntimeBundleTestPermissionDeniedException(
    string code,
    string target,
    string message) : Exception(message)
{
    public string Code { get; } = code;

    public string Target { get; } = target;
}
