using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeWorkflowStore
{
    internal Task<RuntimeWorkflowTaskResult> ApproveExactPendingTaskForAcceptanceAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeWorkflowStartResult exactBinding,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        CompleteTaskAsync(
            tenant,
            user,
            exactBinding.Task?.Id ?? 0,
            "approved",
            reason,
            cancellationToken,
            exactBinding);

    internal Task<RuntimeWorkflowTaskResult> RejectExactPendingTaskForAcceptanceAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeWorkflowStartResult exactBinding,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        CompleteTaskAsync(
            tenant,
            user,
            exactBinding.Task?.Id ?? 0,
            "rejected",
            reason,
            cancellationToken,
            exactBinding);

    private async Task<RuntimeWorkflowTaskResult> CompleteTaskAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        long taskId,
        string decision,
        string? reason,
        CancellationToken cancellationToken,
        RuntimeWorkflowStartResult? exactAcceptanceBinding = null)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var task = await LoadTaskAsync(connection, taskId, cancellationToken, transaction, forUpdate: true);
        if (task is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowTaskNotFoundException(taskId);
        }

        var instance = await LoadInstanceAsync(connection, task.InstanceId, cancellationToken, transaction)
                       ?? throw new RuntimeWorkflowTaskNotFoundException(taskId);
        if (exactAcceptanceBinding is not null &&
            !MatchesExactPendingAcceptanceBinding(exactAcceptanceBinding, instance, task))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowConflictException(
                "Workflow task no longer matches the exact pending acceptance binding.");
        }

        var entity = await LoadEntityByIdAsync(connection, task.EntityId, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(task.EntityId);
        var workflow = await LoadWorkflowAsync(connection, task.WorkflowId, cancellationToken)
                       ?? throw new RuntimeWorkflowNotFoundException(task.WorkflowId);
        var permissionAction = decision == "approved" ? "approve_task" : "reject_task";
        var permissionSnapshot = await permissions.CreateSnapshotAsync(
            connection,
            transaction,
            user,
            cancellationToken);
        var directlyAssigned = IsTaskDirectlyAssignedToUser(user, permissionSnapshot, task);
        if (!permissions.IsAllowed(permissionSnapshot, "read_task", entity) ||
            !directlyAssigned && !permissions.IsAllowed(permissionSnapshot, "read", entity))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowPermissionException("Workflow task read permission denied.");
        }

        var hasEntityRead = permissions.IsAllowed(permissionSnapshot, "read", entity);
        var reviewFields = ReadMetadataStringArray(task.Metadata, "reviewFields");
        if (directlyAssigned && !hasEntityRead && reviewFields.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowPermissionException(
                "Directly assigned workflow task has no closed review-fields contract.");
        }

        if (reviewFields.Count > 0)
        {
            var readableEntity = directlyAssigned && !hasEntityRead
                ? permissions.FilterExplicitTaskReadableEntity(permissionSnapshot, entity)
                : permissions.FilterReadableEntity(permissionSnapshot, entity);
            if (reviewFields.Any(reviewField =>
                    !readableEntity.Fields.Any(field =>
                        RuntimePermissionService.FieldMatchesReference(field, reviewField))))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new RuntimeWorkflowPermissionException(
                    "Workflow task review fields are not all readable by the assigned principal.");
            }
        }

        var canAccessTask = HasWorkflowAdminAuthority(permissionSnapshot) ||
                            CanAccessTask(user, permissionSnapshot, task);
        if (!permissions.IsAllowed(permissionSnapshot, permissionAction, entity))
        {
            await transaction.RollbackAsync(cancellationToken);
            if (exactAcceptanceBinding is not null && !canAccessTask)
            {
                throw new RuntimeWorkflowPermissionException(
                    "Workflow task assignment and action permission checks both denied the decision.");
            }

            throw new RuntimeWorkflowTaskDecisionPermissionException(
                permissionAction,
                task.StepId,
                "Workflow task permission denied.");
        }

        var record = await ReadRecordByIdAsync(
            connection,
            entity,
            task.RecordId,
            cancellationToken,
            transaction,
            forUpdate: true);
        if (record is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowTaskNotFoundException(taskId);
        }

        if (!directlyAssigned &&
            !await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                permissionSnapshot,
                user,
                "read",
                entity,
                record,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowTaskNotFoundException(taskId);
        }

        if (exactAcceptanceBinding is null && !canAccessTask)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowTaskAssignmentPermissionException(
                task.StepId,
                "Workflow task is not assigned to this user.");
        }

        var canDecideRecord = await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                permissionSnapshot,
                user,
                permissionAction,
                entity,
                record,
                cancellationToken);
        if (exactAcceptanceBinding is not null && !canAccessTask)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (!canDecideRecord)
            {
                throw new RuntimeWorkflowPermissionException(
                    "Workflow task assignment and record permission checks both denied the decision.");
            }

            throw new RuntimeWorkflowTaskAssignmentPermissionException(
                task.StepId,
                "Workflow task is not assigned to this user.");
        }

        if (!canDecideRecord)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowTaskDecisionPermissionException(
                permissionAction,
                task.StepId,
                "Workflow task is not allowed for this record.");
        }

        if (task.Status != "pending")
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowConflictException("Workflow task is already completed.");
        }

        if (!TaskSupportsDecision(task, decision))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowValidationException(
                $"Workflow task '{task.StepId}' does not allow rejection.");
        }

        await MarkTaskCompletedAsync(connection, transaction, taskId, decision, user, reason, cancellationToken);
        task = task with
        {
            Status = decision,
            DecisionBy = user.UserId,
            DecisionAt = DateTimeOffset.UtcNow,
            Reason = reason,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await InsertWorkflowEventAsync(
            connection,
            transaction,
            instance.Id,
            task.Id,
            decision == "approved" ? "workflow.task.approved" : "workflow.task.rejected",
            decision == "approved" ? "Workflow task approved." : "Workflow task rejected.",
            new Dictionary<string, object?>
            {
                ["workflowId"] = workflow.Id,
                ["stepId"] = task.StepId,
                ["entity"] = entity.Key,
                ["recordId"] = task.RecordId,
                ["reason"] = reason
            },
            cancellationToken);
        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user,
            decision == "approved" ? "workflow.task.approved" : "workflow.task.rejected",
            decision == "approved" ? "Workflow task approved." : "Workflow task rejected.",
            new Dictionary<string, object?>
            {
                ["workflowId"] = workflow.Id,
                ["stepId"] = task.StepId,
                ["entity"] = entity.Key,
                ["recordId"] = task.RecordId,
                ["workflowInstanceId"] = instance.Id,
                ["workflowTaskId"] = task.Id,
                ["reason"] = reason
            },
            cancellationToken);

        if (decision == "approved")
        {
            var execution = await ContinueWorkflowAfterApprovalAsync(
                connection,
                transaction,
                tenant,
                user,
                workflow,
                entity,
                task.RecordId,
                instance.Id,
                task,
                cancellationToken);

            instance = await UpdateInstanceStateAsync(
                connection,
                transaction,
                instance.Id,
                execution.PendingTask is null ? "completed" : "waiting",
                execution.PendingTask?.StepId,
                execution.PendingTask is null,
                cancellationToken);

            if (execution.PendingTask is null)
            {
                await InsertWorkflowEventAsync(
                    connection,
                    transaction,
                    instance.Id,
                    null,
                    "workflow.completed",
                    $"Completed {workflow.Name}.",
                    new Dictionary<string, object?>
                    {
                        ["workflowId"] = workflow.Id,
                        ["entity"] = entity.Key,
                        ["recordId"] = task.RecordId
                    },
                    cancellationToken);
                await WriteAuditEventAsync(
                    connection,
                    transaction,
                    tenant,
                    user,
                    "workflow.completed",
                    $"Completed {workflow.Name}.",
                    new Dictionary<string, object?>
                    {
                        ["workflowId"] = workflow.Id,
                        ["entity"] = entity.Key,
                        ["recordId"] = task.RecordId,
                        ["workflowInstanceId"] = instance.Id
                    },
                    cancellationToken);
            }
        }
        else
        {
            await ExecuteStepsAsync(
                connection,
                transaction,
                tenant,
                user,
                workflow,
                entity,
                task.RecordId,
                instance.Id,
                workflow.OnRejected,
                0,
                null,
                cancellationToken);
            instance = await UpdateInstanceStateAsync(connection, transaction, instance.Id, "rejected", null, true, cancellationToken);
            await InsertWorkflowEventAsync(
                connection,
                transaction,
                instance.Id,
                task.Id,
                "workflow.rejected",
                $"Rejected {workflow.Name}.",
                new Dictionary<string, object?>
                {
                    ["workflowId"] = workflow.Id,
                    ["entity"] = entity.Key,
                    ["recordId"] = task.RecordId,
                    ["reason"] = reason
                },
                cancellationToken);
            await WriteAuditEventAsync(
                connection,
                transaction,
                tenant,
                user,
                "workflow.rejected",
                $"Rejected {workflow.Name}.",
                new Dictionary<string, object?>
                {
                    ["workflowId"] = workflow.Id,
                    ["entity"] = entity.Key,
                    ["recordId"] = task.RecordId,
                    ["workflowInstanceId"] = instance.Id,
                    ["workflowTaskId"] = task.Id,
                    ["reason"] = reason
                },
                cancellationToken);
        }

        var finalRecord = await ReadRecordByIdAsync(
            connection,
            entity,
            task.RecordId,
            cancellationToken,
            transaction,
            forUpdate: true);
        if (finalRecord is null ||
            !directlyAssigned &&
            !await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                permissionSnapshot,
                user,
                "read",
                entity,
                finalRecord,
                cancellationToken) ||
            !await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                permissionSnapshot,
                user,
                permissionAction,
                entity,
                finalRecord,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowPermissionException(
                "Workflow decision would move the record outside the permitted scope.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new RuntimeWorkflowTaskResult(task, instance);
    }

    internal static bool TaskSupportsDecision(RuntimeWorkflowTask task, string decision) =>
        !decision.Equals("rejected", StringComparison.OrdinalIgnoreCase) || task.CanReject;

    internal static bool MatchesExactPendingAcceptanceBinding(
        RuntimeWorkflowStartResult expected,
        RuntimeWorkflowInstance actualInstance,
        RuntimeWorkflowTask actualTask) =>
        expected.Task is { } expectedTask &&
        expected.Instance.Id > 0 &&
        expectedTask.Id > 0 &&
        actualInstance.Id == expected.Instance.Id &&
        actualInstance.Id == actualTask.InstanceId &&
        actualInstance.WorkflowId.Equals(expected.Instance.WorkflowId, StringComparison.Ordinal) &&
        actualInstance.WorkflowId.Equals(actualTask.WorkflowId, StringComparison.Ordinal) &&
        actualInstance.EntityId.Equals(expected.Instance.EntityId, StringComparison.Ordinal) &&
        actualInstance.EntityId.Equals(actualTask.EntityId, StringComparison.Ordinal) &&
        actualInstance.RecordId == expected.Instance.RecordId &&
        actualInstance.RecordId == actualTask.RecordId &&
        actualInstance.Status.Equals("waiting", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actualInstance.CurrentStepId, actualTask.StepId, StringComparison.Ordinal) &&
        actualTask.Id == expectedTask.Id &&
        actualTask.InstanceId == expectedTask.InstanceId &&
        actualTask.WorkflowId.Equals(expectedTask.WorkflowId, StringComparison.Ordinal) &&
        actualTask.StepId.Equals(expectedTask.StepId, StringComparison.Ordinal) &&
        actualTask.EntityId.Equals(expectedTask.EntityId, StringComparison.Ordinal) &&
        actualTask.RecordId == expectedTask.RecordId &&
        actualTask.Status.Equals("pending", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actualTask.AssignedRole, expectedTask.AssignedRole, StringComparison.Ordinal) &&
        actualTask.AssignedUserId == expectedTask.AssignedUserId;

    private async Task<RuntimeWorkflowExecutionResult> ContinueWorkflowAfterApprovalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeWorkflowDefinition workflow,
        RuntimeEntityDefinition entity,
        long recordId,
        long instanceId,
        RuntimeWorkflowTask task,
        CancellationToken cancellationToken)
    {
        var continuationAfterStepId = ReadMetadataString(task.Metadata, "continuationAfterStepId");
        var branchNextIndex = ReadMetadataInt(task.Metadata, "branchNextIndex");
        if (!string.IsNullOrWhiteSpace(continuationAfterStepId) && branchNextIndex is not null)
        {
            var parentStep = workflow.Steps.FirstOrDefault(step => step.Id.Equals(continuationAfterStepId, StringComparison.OrdinalIgnoreCase));
            if (parentStep is null)
            {
                throw new RuntimeWorkflowValidationException($"Workflow continuation step '{continuationAfterStepId}' was not found.");
            }

            var branchExecution = await ExecuteStepsAsync(
                connection,
                transaction,
                tenant,
                user,
                workflow,
                entity,
                recordId,
                instanceId,
                parentStep.Then,
                branchNextIndex.Value,
                continuationAfterStepId,
                cancellationToken);
            if (branchExecution.PendingTask is not null)
            {
                return branchExecution;
            }

            var parentIndex = IndexOfStep(workflow.Steps, continuationAfterStepId);
            if (parentIndex < 0)
            {
                throw new RuntimeWorkflowValidationException($"Workflow continuation step '{continuationAfterStepId}' was not found.");
            }

            return await ExecuteStepsAsync(
                connection,
                transaction,
                tenant,
                user,
                workflow,
                entity,
                recordId,
                instanceId,
                workflow.Steps,
                parentIndex + 1,
                null,
                cancellationToken);
        }

        var nextIndex = IndexOfStep(workflow.Steps, task.StepId);
        if (nextIndex < 0)
        {
            throw new RuntimeWorkflowValidationException($"Workflow step '{task.StepId}' was not found.");
        }

        return await ExecuteStepsAsync(
            connection,
            transaction,
            tenant,
            user,
            workflow,
            entity,
            recordId,
            instanceId,
            workflow.Steps,
            nextIndex + 1,
            null,
            cancellationToken);
    }

    private async Task<RuntimeWorkflowExecutionResult> ExecuteStepsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext? user,
        RuntimeWorkflowDefinition workflow,
        RuntimeEntityDefinition entity,
        long recordId,
        long instanceId,
        IReadOnlyList<RuntimeWorkflowStep> steps,
        int startIndex,
        string? continuationAfterStepId,
        CancellationToken cancellationToken)
    {
        for (var index = Math.Max(0, startIndex); index < steps.Count; index++)
        {
            var step = steps[index];
            switch (step.Type)
            {
                case "approval":
                    var task = await InsertTaskAsync(
                        connection,
                        transaction,
                        tenant,
                        user,
                        workflow,
                        entity,
                        recordId,
                        instanceId,
                        step,
                        continuationAfterStepId,
                        continuationAfterStepId is null ? null : index + 1,
                        cancellationToken);
                    await InsertWorkflowEventAsync(
                        connection,
                        transaction,
                        instanceId,
                        task.Id,
                        "workflow.task.created",
                        $"Created approval task {step.Label}.",
                        new Dictionary<string, object?>
                        {
                            ["workflowId"] = workflow.Id,
                            ["stepId"] = step.Id,
                            ["entity"] = entity.Key,
                            ["recordId"] = recordId,
                            ["assignedRole"] = task.AssignedRole,
                            ["assignedUserId"] = task.AssignedUserId
                        },
                        cancellationToken);
                    await WriteAuditEventAsync(
                        connection,
                        transaction,
                        tenant,
                        user,
                        "workflow.task.created",
                        $"Created approval task {step.Label}.",
                        new Dictionary<string, object?>
                        {
                            ["workflowId"] = workflow.Id,
                            ["stepId"] = step.Id,
                            ["entity"] = entity.Key,
                            ["recordId"] = recordId,
                            ["workflowInstanceId"] = instanceId,
                            ["workflowTaskId"] = task.Id,
                            ["assignedRole"] = task.AssignedRole,
                            ["assignedUserId"] = task.AssignedUserId
                        },
                        cancellationToken);
                    var notification = await InsertNotificationAsync(
                        connection,
                        transaction,
                        instanceId,
                        task.Id,
                        task.AssignedRole,
                        task.AssignedUserId,
                        "task_assigned",
                        $"Approval needed: {workflow.Name}",
                        $"{step.Label} is waiting on {entity.DisplayName} #{recordId}.",
                        $"/tasks/{task.Id}",
                        new Dictionary<string, object?>
                        {
                            ["workflowId"] = workflow.Id,
                            ["stepId"] = step.Id,
                            ["entity"] = entity.Key,
                            ["recordId"] = recordId,
                            ["taskId"] = task.Id
                        },
                        cancellationToken);
                    await InsertWorkflowEventAsync(
                        connection,
                        transaction,
                        instanceId,
                        task.Id,
                        "notification.created",
                        "Runtime notification created.",
                        new Dictionary<string, object?>
                        {
                            ["notificationId"] = notification.Id,
                            ["notificationType"] = notification.NotificationType,
                            ["recipientRole"] = notification.RecipientRole,
                            ["recipientUserId"] = notification.RecipientUserId
                        },
                        cancellationToken);
                    await WriteAuditEventAsync(
                        connection,
                        transaction,
                        tenant,
                        user,
                        "notification.created",
                        "Runtime notification created.",
                        new Dictionary<string, object?>
                        {
                            ["notificationId"] = notification.Id,
                            ["notificationType"] = notification.NotificationType,
                            ["workflowInstanceId"] = instanceId,
                            ["workflowTaskId"] = task.Id,
                            ["recipientRole"] = notification.RecipientRole,
                            ["recipientUserId"] = notification.RecipientUserId
                        },
                        cancellationToken);
                    return new RuntimeWorkflowExecutionResult(task);

                case "update_record":
                    await ApplyRecordUpdateAsync(connection, transaction, entity, recordId, user, step.Set, cancellationToken);
                    await InsertWorkflowEventAsync(
                        connection,
                        transaction,
                        instanceId,
                        null,
                        "workflow.record.updated",
                        $"Updated {entity.DisplayName} from workflow step {step.Id}.",
                        new Dictionary<string, object?>
                        {
                            ["workflowId"] = workflow.Id,
                            ["stepId"] = step.Id,
                            ["entity"] = entity.Key,
                            ["recordId"] = recordId,
                            ["fields"] = step.Set.Keys.ToArray()
                        },
                        cancellationToken);
                    break;

                case "audit_event":
                    if (!string.IsNullOrWhiteSpace(step.Event))
                    {
                        await InsertWorkflowEventAsync(
                            connection,
                            transaction,
                            instanceId,
                            null,
                            step.Event,
                            string.IsNullOrWhiteSpace(step.Message) ? $"Workflow event {step.Event}." : step.Message,
                            new Dictionary<string, object?>
                            {
                                ["workflowId"] = workflow.Id,
                                ["stepId"] = step.Id,
                                ["entity"] = entity.Key,
                                ["recordId"] = recordId
                            },
                            cancellationToken);
                        await WriteAuditEventAsync(
                            connection,
                            transaction,
                            tenant,
                            user,
                            step.Event,
                            string.IsNullOrWhiteSpace(step.Message) ? $"Workflow event {step.Event}." : step.Message,
                            new Dictionary<string, object?>
                            {
                                ["workflowId"] = workflow.Id,
                                ["stepId"] = step.Id,
                                ["entity"] = entity.Key,
                                ["recordId"] = recordId
                            },
                            cancellationToken);
                    }
                    break;

                case "notification_placeholder":
                    await InsertNotificationAsync(
                        connection,
                        transaction,
                        instanceId,
                        null,
                        step.AssignedTo?.Role,
                        null,
                        "workflow_notice",
                        string.IsNullOrWhiteSpace(step.Message) ? workflow.Name : step.Message,
                        string.IsNullOrWhiteSpace(step.Message) ? workflow.Name : step.Message,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["workflowId"] = workflow.Id,
                            ["stepId"] = step.Id,
                            ["entity"] = entity.Key,
                            ["recordId"] = recordId
                        },
                        cancellationToken);
                    break;

                case "condition":
                    if (step.Condition is null)
                    {
                        throw new RuntimeWorkflowValidationException($"Workflow condition step '{step.Id}' has no condition metadata.");
                    }

                    var evaluation = await EvaluateConditionAsync(
                        connection,
                        transaction,
                        entity,
                        recordId,
                        step.Condition,
                        cancellationToken);
                    await InsertWorkflowEventAsync(
                        connection,
                        transaction,
                        instanceId,
                        null,
                        "workflow.condition.evaluated",
                        $"Evaluated workflow condition {step.Id}.",
                        new Dictionary<string, object?>
                        {
                            ["workflowId"] = workflow.Id,
                            ["stepId"] = step.Id,
                            ["conditionId"] = step.Id,
                            ["result"] = evaluation.Result,
                            ["operator"] = evaluation.Op
                        },
                        cancellationToken);
                    if (evaluation.Result)
                    {
                        await InsertWorkflowEventAsync(
                            connection,
                            transaction,
                            instanceId,
                            null,
                            "workflow.branch.entered",
                            $"Entered workflow branch {step.Id}.",
                            new Dictionary<string, object?>
                            {
                                ["workflowId"] = workflow.Id,
                                ["stepId"] = step.Id,
                                ["conditionId"] = step.Id
                            },
                            cancellationToken);
                        var branchExecution = await ExecuteStepsAsync(
                            connection,
                            transaction,
                            tenant,
                            user,
                            workflow,
                            entity,
                            recordId,
                            instanceId,
                            step.Then,
                            0,
                            step.Id,
                            cancellationToken);
                        if (branchExecution.PendingTask is not null)
                        {
                            return branchExecution;
                        }
                    }
                    break;
            }
        }

        return new RuntimeWorkflowExecutionResult(null);
    }

}
