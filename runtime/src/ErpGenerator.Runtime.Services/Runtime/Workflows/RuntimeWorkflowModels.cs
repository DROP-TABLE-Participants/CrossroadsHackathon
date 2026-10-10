using System.Text.Json;
using ErpGenerator.Runtime.Shared.Compilation;
using System.Text.Json.Serialization;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed record RuntimeWorkflowDefinition(
    string Id,
    string Name,
    string Entity,
    RuntimeWorkflowTrigger? Trigger,
    IReadOnlyList<RuntimeWorkflowStep> Steps,
    IReadOnlyList<RuntimeWorkflowStep> OnRejected)
{
    public IReadOnlyList<RuntimeWorkflowStartGuard> StartGuards { get; init; } = [];
}

public sealed record RuntimeWorkflowStartGuard(
    string Id,
    string Entity,
    IReadOnlyList<ActionFilterDefinition> Where,
    string ResultCountOp,
    int ResultCount);

public sealed record RuntimeWorkflowTrigger(string Action);

public sealed record RuntimeWorkflowStep(
    string Id,
    string Type,
    string Label,
    RuntimeWorkflowAssignment? AssignedTo,
    string? ApproveLabel,
    string? RejectLabel,
    IReadOnlyDictionary<string, string> Set,
    RuntimeWorkflowCondition? Condition,
    IReadOnlyList<RuntimeWorkflowStep> Then,
    string? Event,
    string? Message,
    IReadOnlyList<string>? ReviewFields = null);

public sealed record RuntimeWorkflowCondition(
    string Op,
    RuntimeWorkflowConditionOperand? Left,
    RuntimeWorkflowConditionOperand? Right,
    IReadOnlyList<RuntimeWorkflowCondition> Conditions);

public sealed record RuntimeWorkflowConditionOperand(
    string? Field,
    string? Path,
    object? Value);

public sealed record RuntimeWorkflowAssignment(
    string? Type,
    string? Role,
    string? Path,
    string? RequiredRole,
    string? FallbackRole);

public sealed record RuntimeWorkflowInstance(
    long Id,
    string WorkflowId,
    string WorkflowName,
    string EntityId,
    string EntityKey,
    long RecordId,
    string Status,
    string? CurrentStepId,
    long? StartedBy,
    DateTimeOffset? CompletedAt,
    JsonElement Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RuntimeWorkflowTask(
    long Id,
    long InstanceId,
    string WorkflowId,
    string StepId,
    string Label,
    string EntityId,
    long RecordId,
    string Status,
    string? AssignedRole,
    long? AssignedUserId,
    long? DecisionBy,
    DateTimeOffset? DecisionAt,
    string? Reason,
    [property: JsonIgnore] JsonElement Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool CanReject =>
        Metadata.ValueKind == JsonValueKind.Object &&
        (Metadata.TryGetProperty("canReject", out var canReject)
            ? canReject.ValueKind == JsonValueKind.True
            : Metadata.TryGetProperty("rejectLabel", out var legacyRejectLabel) &&
              legacyRejectLabel.ValueKind == JsonValueKind.String &&
              !string.IsNullOrWhiteSpace(legacyRejectLabel.GetString()));
}

public sealed record RuntimeWorkflowEvent(
    long Id,
    long InstanceId,
    long? TaskId,
    string EventType,
    string Message,
    JsonElement Metadata,
    DateTimeOffset CreatedAt);

public sealed record RuntimeNotification(
    long Id,
    long? InstanceId,
    long? TaskId,
    string Channel,
    string NotificationType,
    string? RecipientRole,
    long? RecipientUserId,
    string Title,
    string Message,
    string? Link,
    string Subject,
    JsonElement Payload,
    string Status,
    DateTimeOffset? ReadAt,
    long? ReadBy,
    DateTimeOffset CreatedAt);

public sealed record RuntimeRecordAction(
    string Key,
    string Label,
    string WorkflowId,
    string Type = "workflow");

public sealed record RuntimeWorkflowStartResult(
    RuntimeWorkflowInstance Instance,
    RuntimeWorkflowTask? Task);

public sealed record RuntimeWorkflowTaskResult(
    RuntimeWorkflowTask Task,
    RuntimeWorkflowInstance Instance);

public sealed record RuntimeWorkflowTaskRecordDetail(
    RuntimeWorkflowTask Task,
    string EntityKey,
    string EntityLabel,
    long RecordId,
    IReadOnlyList<RuntimeWorkflowTaskEvidenceField> Evidence);

public sealed record RuntimeWorkflowTaskEvidenceField(
    string Key,
    string Label,
    string Type,
    object? Value);

public sealed record RuntimeWorkflowInstanceDetail(
    RuntimeWorkflowInstance Instance,
    IReadOnlyList<RuntimeWorkflowTask> Tasks,
    IReadOnlyList<RuntimeWorkflowEvent> Events);

internal sealed record RuntimeConditionEvaluation(
    bool Result,
    string Op);

public sealed class RuntimeWorkflowNotFoundException(string key) : Exception($"Workflow action '{key}' was not found.");

public sealed class RuntimeWorkflowRecordNotFoundException(string entityKey, long recordId) : Exception($"Record '{entityKey}/{recordId}' was not found.");

public sealed class RuntimeWorkflowTaskNotFoundException(long taskId) : Exception($"Workflow task '{taskId}' was not found.");

public class RuntimeWorkflowConflictException(string message) : Exception(message);

public sealed class RuntimeWorkflowActionNotAvailableException(
    string actionKey,
    string message) : RuntimeWorkflowConflictException(message)
{
    public string ActionKey { get; } = actionKey;
}

public sealed class RuntimeWorkflowStartGuardRejectedException(string guardId)
    : Exception("Workflow start requirements are not satisfied.")
{
    public string GuardId { get; } = guardId;
}

public class RuntimeWorkflowPermissionException(string message) : Exception(message);

/// <summary>
/// Entity-level workflow authority exists, but the exact source record is
/// outside the principal's start-workflow scope. This boundary is kept
/// distinct from source read and other workflow permission failures so an
/// acceptance test cannot receive credit for the wrong denial.
/// </summary>
public sealed class RuntimeWorkflowRecordStartPermissionException(string message)
    : RuntimeWorkflowPermissionException(message);

/// <summary>
/// A task decision reached the exact workflow task but the tested principal is
/// not one of its assigned users or roles. Keeping this distinct from generic
/// workflow permission failures prevents an expected assignment denial from
/// being satisfied by a missing read or decision grant.
/// </summary>
public sealed class RuntimeWorkflowTaskAssignmentPermissionException(
    string workflowStep,
    string message) : RuntimeWorkflowPermissionException(message)
{
    public string WorkflowStep { get; } = workflowStep;
}

/// <summary>
/// A task decision reached the exact workflow task but the tested principal is
/// not allowed to approve or reject that record. The action and workflow step
/// are retained so acceptance classification can emit the exact closed code
/// and target instead of relying on exception text.
/// </summary>
public sealed class RuntimeWorkflowTaskDecisionPermissionException(
    string action,
    string workflowStep,
    string message) : RuntimeWorkflowPermissionException(message)
{
    public string Action { get; } = action;

    public string WorkflowStep { get; } = workflowStep;
}

public sealed class RuntimeWorkflowValidationException(string message) : Exception(message);
