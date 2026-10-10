using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeWorkflowStore
{
    private static async Task<RuntimeWorkflowInstance> InsertInstanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeWorkflowDefinition workflow,
        RuntimeEntityDefinition entity,
        long recordId,
        RuntimeUserContext user,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.workflow_instances (workflow_id, workflow_name, entity_id, entity_key, record_id, status, started_by, metadata)
            values (@workflowId, @workflowName, @entityId, @entityKey, @recordId, 'running', @startedBy, @metadata)
            returning id, workflow_id, workflow_name, entity_id, entity_key, record_id, status, current_step_id, started_by, completed_at, metadata, created_at, updated_at
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("workflowId", workflow.Id);
        command.Parameters.AddWithValue("workflowName", workflow.Name);
        command.Parameters.AddWithValue("entityId", entity.Id);
        command.Parameters.AddWithValue("entityKey", entity.Key);
        command.Parameters.AddWithValue("recordId", recordId);
        command.Parameters.AddWithValue("startedBy", user.UserId);
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["triggerAction"] = workflow.Trigger?.Action
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Workflow instance insert did not return a row.");
        }

        return ReadInstance(reader);
    }

    private static async Task<RuntimeWorkflowInstance> UpdateInstanceStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        string status,
        string? currentStepId,
        bool completed,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update _runtime.workflow_instances
            set status = @status,
                current_step_id = @currentStepId,
                completed_at = case when @completed then now() else completed_at end,
                updated_at = now()
            where id = @id
            returning id, workflow_id, workflow_name, entity_id, entity_key, record_id, status, current_step_id, started_by, completed_at, metadata, created_at, updated_at
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", instanceId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("currentStepId", (object?)currentStepId ?? DBNull.Value);
        command.Parameters.AddWithValue("completed", completed);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new RuntimeWorkflowTaskNotFoundException(instanceId);
        }

        return ReadInstance(reader);
    }

    private static async Task<RuntimeWorkflowTask> InsertTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext? user,
        RuntimeWorkflowDefinition workflow,
        RuntimeEntityDefinition entity,
        long recordId,
        long instanceId,
        RuntimeWorkflowStep step,
        string? continuationAfterStepId,
        int? branchNextIndex,
        CancellationToken cancellationToken)
    {
        var assignment = await ResolveTaskAssignmentAsync(connection, transaction, entity, recordId, step, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.workflow_tasks (instance_id, workflow_id, step_id, label, entity_id, record_id, status, assigned_role, assigned_user_id, metadata)
            values (@instanceId, @workflowId, @stepId, @label, @entityId, @recordId, 'pending', @assignedRole, @assignedUserId, @metadata)
            returning id, instance_id, workflow_id, step_id, label, entity_id, record_id, status, assigned_role, assigned_user_id, decision_by, decision_at, reason, metadata, created_at, updated_at
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("instanceId", instanceId);
        command.Parameters.AddWithValue("workflowId", workflow.Id);
        command.Parameters.AddWithValue("stepId", step.Id);
        command.Parameters.AddWithValue("label", step.Label);
        command.Parameters.AddWithValue("entityId", entity.Id);
        command.Parameters.AddWithValue("recordId", recordId);
        command.Parameters.AddWithValue("assignedRole", (object?)assignment.Role ?? DBNull.Value);
        command.Parameters.AddWithValue("assignedUserId", (object?)assignment.UserId ?? DBNull.Value);
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["approveLabel"] = step.ApproveLabel,
            ["rejectLabel"] = step.RejectLabel,
            ["canReject"] = workflow.OnRejected.Count > 0,
            ["assignmentType"] = assignment.AssignmentType,
            ["assignmentPath"] = assignment.Path,
            ["assignmentSource"] = assignment.Source,
            ["requiredRole"] = step.AssignedTo?.RequiredRole,
            ["reviewFields"] = step.ReviewFields ?? [],
            ["continuationAfterStepId"] = continuationAfterStepId,
            ["branchNextIndex"] = branchNextIndex
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Workflow task insert did not return a row.");
        }

        var task = ReadTask(reader);
        await reader.DisposeAsync();
        if (assignment.UserId is not null)
        {
            await InsertWorkflowEventAsync(
                connection,
                transaction,
                instanceId,
                task.Id,
                "workflow.task.assigned",
                "Workflow task assigned to runtime user.",
                new Dictionary<string, object?>
                {
                    ["workflowId"] = workflow.Id,
                    ["stepId"] = step.Id,
                    ["entity"] = entity.Key,
                    ["recordId"] = recordId,
                    ["assignedUserId"] = assignment.UserId,
                    ["assignmentPath"] = assignment.Path,
                    ["assignmentSource"] = assignment.Source
                },
                cancellationToken);
            await WriteAuditEventAsync(
                connection,
                transaction,
                tenant,
                user,
                "workflow.task.assigned",
                "Workflow task assigned to runtime user.",
                new Dictionary<string, object?>
                {
                    ["workflowId"] = workflow.Id,
                    ["stepId"] = step.Id,
                    ["entity"] = entity.Key,
                    ["recordId"] = recordId,
                    ["workflowInstanceId"] = instanceId,
                    ["workflowTaskId"] = task.Id,
                    ["assignedUserId"] = assignment.UserId,
                    ["assignmentPath"] = assignment.Path,
                    ["assignmentSource"] = assignment.Source
                },
                cancellationToken);
        }
        else if (assignment.FellBack)
        {
            await InsertWorkflowEventAsync(
                connection,
                transaction,
                instanceId,
                task.Id,
                "workflow.task.assignment_fallback",
                "Workflow task assignment fell back to role.",
                new Dictionary<string, object?>
                {
                    ["workflowId"] = workflow.Id,
                    ["stepId"] = step.Id,
                    ["entity"] = entity.Key,
                    ["recordId"] = recordId,
                    ["fallbackRole"] = assignment.Role,
                    ["assignmentPath"] = assignment.Path
                },
                cancellationToken);
            await WriteAuditEventAsync(
                connection,
                transaction,
                tenant,
                user,
                "workflow.task.assignment_fallback",
                "Workflow task assignment fell back to role.",
                new Dictionary<string, object?>
                {
                    ["workflowId"] = workflow.Id,
                    ["stepId"] = step.Id,
                    ["entity"] = entity.Key,
                    ["recordId"] = recordId,
                    ["workflowInstanceId"] = instanceId,
                    ["workflowTaskId"] = task.Id,
                    ["fallbackRole"] = assignment.Role,
                    ["assignmentPath"] = assignment.Path
                },
                cancellationToken);
        }

        return task;
    }

    private static async Task MarkTaskCompletedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long taskId,
        string status,
        RuntimeUserContext user,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update _runtime.workflow_tasks
            set status = @status,
                decision_by = @decisionBy,
                decision_at = now(),
                reason = @reason,
                updated_at = now()
            where id = @id and status = 'pending'
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", taskId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("decisionBy", user.UserId);
        command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated == 0)
        {
            throw new RuntimeWorkflowConflictException("Workflow task is already completed.");
        }
    }

    private static async Task<RuntimeTaskAssignment> ResolveTaskAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeEntityDefinition entity,
        long recordId,
        RuntimeWorkflowStep step,
        CancellationToken cancellationToken)
    {
        var assignment = step.AssignedTo;
        if (assignment is null)
        {
            return RuntimeTaskAssignment.Unassigned();
        }

        var type = string.IsNullOrWhiteSpace(assignment.Type)
            ? (string.IsNullOrWhiteSpace(assignment.Path) ? "role" : "relation_user")
            : assignment.Type.Trim().ToLowerInvariant();

        if (type == "relation_user")
        {
            var resolved = await ResolveRelationUserAssignmentAsync(
                connection,
                transaction,
                entity,
                recordId,
                assignment.Path,
                cancellationToken);
            if (resolved.UserId is not null)
            {
                if (!string.IsNullOrWhiteSpace(assignment.RequiredRole) &&
                    await UserHasExactRoleAsync(
                        connection,
                        transaction,
                        resolved.UserId.Value,
                        assignment.RequiredRole,
                        cancellationToken))
                {
                    return resolved;
                }

                resolved = RuntimeTaskAssignment.Unassigned(
                    "relation_user",
                    assignment.Path,
                    string.IsNullOrWhiteSpace(assignment.RequiredRole)
                        ? "required_role_missing"
                        : "required_role_mismatch");
            }

            if (!string.IsNullOrWhiteSpace(assignment.FallbackRole))
            {
                return RuntimeTaskAssignment.FallbackRole(assignment.FallbackRole.Trim().ToLowerInvariant(), assignment.Path);
            }

            throw new RuntimeWorkflowConflictException(
                $"Workflow approval step '{step.Id}' could not resolve one exact active relation user with required role " +
                $"'{assignment.RequiredRole}' through path '{assignment.Path}', and no fallback role is configured.");
        }

        return string.IsNullOrWhiteSpace(assignment.Role)
            ? RuntimeTaskAssignment.Unassigned(type, assignment.Path)
            : RuntimeTaskAssignment.RoleAssignment(assignment.Role.Trim().ToLowerInvariant());
    }

    private static async Task<RuntimeTaskAssignment> ResolveRelationUserAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeEntityDefinition startEntity,
        long startRecordId,
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return RuntimeTaskAssignment.Unassigned("relation_user", path);
        }

        var currentEntity = startEntity;
        var currentRecordId = startRecordId;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var field = ResolveRelationField(currentEntity, segment);
            if (field is null || string.IsNullOrWhiteSpace(field.ReferencesEntity))
            {
                return RuntimeTaskAssignment.Unassigned("relation_user", path);
            }

            var record = await ReadRecordByIdAsync(connection, currentEntity, currentRecordId, cancellationToken, transaction);
            if (record is null || !record.TryGetValue(field.ColumnName, out var value) || value is null || value is DBNull)
            {
                return RuntimeTaskAssignment.Unassigned("relation_user", path);
            }

            currentRecordId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            var nextEntity = await LoadEntityByIdAsync(connection, field.ReferencesEntity, cancellationToken);
            if (nextEntity is null)
            {
                return RuntimeTaskAssignment.Unassigned("relation_user", path);
            }

            currentEntity = nextEntity;
        }

        var linkedUser = await ResolveRuntimeUserIdFromLinkAsync(
            connection,
            transaction,
            currentEntity,
            currentRecordId,
            cancellationToken);
        if (linkedUser.IsAmbiguous)
        {
            return RuntimeTaskAssignment.Unassigned("relation_user", path, "ambiguous_explicit_link");
        }

        if (linkedUser.UserId is not null)
        {
            return RuntimeTaskAssignment.UserAssignment(linkedUser.UserId.Value, "explicit_link", path);
        }

        var emailUser = await ResolveRuntimeUserIdFromEmailAsync(
            connection,
            transaction,
            currentEntity,
            currentRecordId,
            cancellationToken);
        if (emailUser.IsAmbiguous)
        {
            return RuntimeTaskAssignment.Unassigned("relation_user", path, "ambiguous_email_binding");
        }

        return emailUser.UserId is null
            ? RuntimeTaskAssignment.Unassigned("relation_user", path)
            : RuntimeTaskAssignment.UserAssignment(emailUser.UserId.Value, "email_match", path);
    }

    private static RuntimeFieldDefinition? ResolveRelationField(RuntimeEntityDefinition entity, string segment) =>
        entity.Fields.FirstOrDefault(field =>
            field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) &&
            (field.Id.Equals(segment, StringComparison.OrdinalIgnoreCase) ||
             field.Name.Equals(segment, StringComparison.OrdinalIgnoreCase) ||
             field.ColumnName.Equals(segment, StringComparison.OrdinalIgnoreCase) ||
             (field.ColumnName.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
              field.ColumnName[..^3].Equals(segment, StringComparison.OrdinalIgnoreCase))));

    private static async Task<RuntimeUserLinkResolution> ResolveRuntimeUserIdFromLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeEntityDefinition entity,
        long recordId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select distinct u.id
            from _runtime.runtime_user_links l
            join _runtime.runtime_users u on u.id = l.runtime_user_id and u.status = 'active'
            where l.record_id = @recordId
              and l.entity_key = @entityId
            order by u.id
            limit 2
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("recordId", recordId);
        command.Parameters.AddWithValue("entityId", entity.Id);
        var userIds = new List<long>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            userIds.Add(reader.GetInt64(0));
        }

        return userIds.Count switch
        {
            0 => new RuntimeUserLinkResolution(null, false),
            1 => new RuntimeUserLinkResolution(userIds[0], false),
            _ => new RuntimeUserLinkResolution(null, true)
        };
    }

    private static async Task<RuntimeUserLinkResolution> ResolveRuntimeUserIdFromEmailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeEntityDefinition entity,
        long recordId,
        CancellationToken cancellationToken)
    {
        var emailFields = entity.Fields.Where(field =>
                field.Id.Equals("email", StringComparison.OrdinalIgnoreCase) ||
                field.Name.Equals("email", StringComparison.OrdinalIgnoreCase) ||
                field.ColumnName.Equals("email", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (emailFields.Length != 1)
        {
            return new RuntimeUserLinkResolution(null, emailFields.Length > 1);
        }

        var emailField = emailFields[0];

        var record = await ReadRecordByIdAsync(connection, entity, recordId, cancellationToken, transaction);
        if (record is null ||
            !record.TryGetValue(emailField.ColumnName, out var rawEmail) ||
            rawEmail is null ||
            string.IsNullOrWhiteSpace(Convert.ToString(rawEmail, CultureInfo.InvariantCulture)))
        {
            return new RuntimeUserLinkResolution(null, false);
        }

        await using var command = new NpgsqlCommand(
            """
            select id
            from _runtime.runtime_users
            where lower(email) = lower(@email)
              and status = 'active'
            order by id
            limit 2
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("email", Convert.ToString(rawEmail, CultureInfo.InvariantCulture)!);
        var userIds = new List<long>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            userIds.Add(reader.GetInt64(0));
        }

        return userIds.Count switch
        {
            0 => new RuntimeUserLinkResolution(null, false),
            1 => new RuntimeUserLinkResolution(userIds[0], false),
            _ => new RuntimeUserLinkResolution(null, true)
        };
    }

    private static async Task<bool> UserHasExactRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        string requiredRole,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select 1
            from _runtime.runtime_users users
            join _runtime.runtime_user_roles roles on roles.user_id = users.id
            where users.id = @userId
              and users.status = 'active'
              and lower(roles.role_id) = lower(@requiredRole)
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("requiredRole", requiredRole.Trim());
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task InsertWorkflowEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        long? taskId,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.workflow_events (instance_id, task_id, event_type, message, metadata)
            values (@instanceId, @taskId, @eventType, @message, @metadata)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("instanceId", instanceId);
        command.Parameters.AddWithValue("taskId", (object?)taskId ?? DBNull.Value);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("message", message);
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(metadata);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RuntimeNotification> InsertNotificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long instanceId,
        long? taskId,
        string? role,
        long? userId,
        string notificationType,
        string title,
        string message,
        string? link,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.notification_outbox (
              instance_id, task_id, notification_type, recipient_role, recipient_user_id,
              title, message, link, subject, payload, status
            )
            values (
              @instanceId, @taskId, @notificationType, @recipientRole, @recipientUserId,
              @title, @message, @link, @subject, @payload, 'unread'
            )
            returning id, instance_id, task_id, channel, notification_type, recipient_role, recipient_user_id,
                      coalesce(nullif(title, ''), subject) as title,
                      coalesce(nullif(message, ''), subject) as message,
                      link,
                      subject,
                      payload,
                      status,
                      read_at,
                      read_by,
                      created_at
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("instanceId", instanceId);
        command.Parameters.AddWithValue("taskId", (object?)taskId ?? DBNull.Value);
        command.Parameters.AddWithValue("notificationType", notificationType);
        command.Parameters.AddWithValue("recipientRole", (object?)role ?? DBNull.Value);
        command.Parameters.AddWithValue("recipientUserId", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("message", message);
        command.Parameters.AddWithValue("link", (object?)link ?? DBNull.Value);
        command.Parameters.AddWithValue("subject", title);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(payload);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Notification insert did not return a row.");
        }

        return ReadNotification(reader);
    }

    private static async Task WriteAuditEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext? user,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        var envelope = new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["tenantStableId"] = tenant.TenantStableId,
            ["tenantSlug"] = tenant.TenantSlug,
            ["host"] = tenant.Host,
            ["environment"] = tenant.Environment.ToString(),
            ["runtimeUserId"] = user?.UserId,
            ["runtimeUserEmail"] = user?.Email,
            ["runtimeUserRole"] = user?.Role,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        await using var command = new NpgsqlCommand(
            """
            insert into audit.events (event_type, message, metadata)
            values (@eventType, @message, @metadata)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("message", message);
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(envelope);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasActiveInstanceAsync(
        NpgsqlConnection connection,
        string workflowId,
        string entityId,
        long recordId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(
            """
            select 1
            from _runtime.workflow_instances
            where workflow_id = @workflowId
              and entity_id = @entityId
              and record_id = @recordId
              and status in ('running', 'waiting')
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("workflowId", workflowId);
        command.Parameters.AddWithValue("entityId", entityId);
        command.Parameters.AddWithValue("recordId", recordId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static bool CanStartWorkflowForRecord(
        RuntimeWorkflowDefinition workflow,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> record)
    {
        var lifecycleGates = WorkflowMutationSteps(workflow)
            .Where(step => step.Type.Equals("update_record", StringComparison.OrdinalIgnoreCase))
            .SelectMany(step => step.Set)
            .Select(update =>
            {
                var field = ResolveField(entity, update.Key);
                return field?.Lifecycle is null ||
                       update.Value.Equals(field.Lifecycle.Initial, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : field;
            })
            .Where(field => field is not null)
            .Cast<RuntimeFieldDefinition>()
            .DistinctBy(field => field.Id, StringComparer.Ordinal)
            .ToArray();
        if (lifecycleGates.Length > 0)
        {
            return lifecycleGates.All(field =>
            {
                var raw = record.TryGetValue(field.ColumnName, out var columnValue)
                    ? columnValue
                    : record.GetValueOrDefault(field.Name);
                return raw is not null and not DBNull &&
                       string.Equals(
                           Convert.ToString(raw, CultureInfo.InvariantCulture),
                           field.Lifecycle!.Initial,
                           StringComparison.OrdinalIgnoreCase);
            });
        }

        // Compatibility for bundles compiled before lifecycle metadata became
        // authoritative. New workflows derive their start state from the
        // exact lifecycle fields they transition instead of a magic name.
        var approvalStatusField = entity.Fields.FirstOrDefault(field =>
            field.Name.Equals("approval_status", StringComparison.OrdinalIgnoreCase) ||
            field.ColumnName.Equals("approval_status", StringComparison.OrdinalIgnoreCase));
        if (approvalStatusField is null ||
            approvalStatusField.Lifecycle is not null ||
            !WorkflowMutationSteps(workflow).Any(step =>
                step.Type.Equals("update_record", StringComparison.OrdinalIgnoreCase) &&
                step.Set.Keys.Any(field =>
                    ResolveField(entity, field)?.Id.Equals(
                        approvalStatusField.Id,
                        StringComparison.OrdinalIgnoreCase) == true)))
        {
            return true;
        }

        var rawStatus = record.TryGetValue(approvalStatusField.ColumnName, out var columnValue)
            ? columnValue
            : record.GetValueOrDefault(approvalStatusField.Name);
        if (rawStatus is null || rawStatus is DBNull)
        {
            return true;
        }

        var status = Convert.ToString(rawStatus, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(status) ||
               status.Equals("draft", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<RuntimeWorkflowStep> WorkflowMutationSteps(
        RuntimeWorkflowDefinition workflow) =>
        WorkflowSteps(workflow.Steps).Concat(WorkflowSteps(workflow.OnRejected));

    private static IEnumerable<RuntimeWorkflowStep> WorkflowSteps(
        IReadOnlyList<RuntimeWorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var nested in WorkflowSteps(step.Then))
            {
                yield return nested;
            }
        }
    }

    private static async Task<RuntimeWorkflowInstance?> LoadInstanceAsync(
        NpgsqlConnection connection,
        long instanceId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, workflow_id, workflow_name, entity_id, entity_key, record_id, status, current_step_id, started_by, completed_at, metadata, created_at, updated_at
            from _runtime.workflow_instances
            where id = @id
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInstance(reader) : null;
    }

    private static async Task<RuntimeWorkflowTask?> LoadTaskAsync(
        NpgsqlConnection connection,
        long taskId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null,
        bool forUpdate = false)
    {
        await using var command = new NpgsqlCommand(
            $"""
            select id, instance_id, workflow_id, step_id, label, entity_id, record_id, status, assigned_role, assigned_user_id, decision_by, decision_at, reason, metadata, created_at, updated_at
            from _runtime.workflow_tasks
            where id = @id
            limit 1
            {(forUpdate ? "for update" : string.Empty)}
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", taskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTask(reader) : null;
    }

    private static async Task<IReadOnlyList<RuntimeWorkflowTask>> LoadTasksForInstanceAsync(
        NpgsqlConnection connection,
        long instanceId,
        CancellationToken cancellationToken)
    {
        var tasks = new List<RuntimeWorkflowTask>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, instance_id, workflow_id, step_id, label, entity_id, record_id, status, assigned_role, assigned_user_id, decision_by, decision_at, reason, metadata, created_at, updated_at
            from _runtime.workflow_tasks
            where instance_id = @instanceId
            order by id
            """;
        command.Parameters.AddWithValue("instanceId", instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    private static async Task<IReadOnlyList<RuntimeWorkflowEvent>> LoadEventsForInstanceAsync(
        NpgsqlConnection connection,
        long instanceId,
        CancellationToken cancellationToken)
    {
        var events = new List<RuntimeWorkflowEvent>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, instance_id, task_id, event_type, message, metadata, created_at
            from _runtime.workflow_events
            where instance_id = @instanceId
            order by id
            """;
        command.Parameters.AddWithValue("instanceId", instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    private static RuntimeWorkflowEvent ReadEvent(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        reader.GetString(3),
        reader.GetString(4),
        JsonDocument.Parse(reader.GetString(5)).RootElement.Clone(),
        reader.GetFieldValue<DateTimeOffset>(6));

    private static RuntimeNotification ReadNotification(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetString(10),
        JsonDocument.Parse(reader.GetString(11)).RootElement.Clone(),
        reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        reader.IsDBNull(14) ? null : reader.GetInt64(14),
        reader.GetFieldValue<DateTimeOffset>(15));

    private static RuntimeWorkflowInstance ReadInstance(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        JsonDocument.Parse(reader.GetString(10)).RootElement.Clone(),
        reader.GetFieldValue<DateTimeOffset>(11),
        reader.GetFieldValue<DateTimeOffset>(12));

    private static RuntimeWorkflowTask ReadTask(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        JsonDocument.Parse(reader.GetString(13)).RootElement.Clone(),
        reader.GetFieldValue<DateTimeOffset>(14),
        reader.GetFieldValue<DateTimeOffset>(15));

    private static Dictionary<string, object?> ReadRecord(NpgsqlDataReader reader)
    {
        var record = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            record[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        }

        return record;
    }

    private static string BuildSelectColumns(RuntimeEntityDefinition entity)
    {
        var columns = new[] { "id", "created_at", "created_by", "updated_at", "updated_by", "archived_at", "archived_by", "archive_reason" }
            .Concat(entity.Fields.Where(field => field.Computed is null).Select(field => field.ColumnName))
            .Select(Quote);

        return string.Join(", ", columns);
    }

    private static bool CanAccessTask(
        RuntimeUserContext user,
        RuntimePermissionSnapshot snapshot,
        RuntimeWorkflowTask task) =>
        task.AssignedUserId is { } assignedUserId
            ? assignedUserId == user.UserId && HasDirectAssignmentRequiredRole(snapshot, task)
            : !string.IsNullOrWhiteSpace(task.AssignedRole) &&
              snapshot.EffectiveRoleIds
                  .Any(role => role.Equals(task.AssignedRole, StringComparison.OrdinalIgnoreCase));

    private static bool IsTaskDirectlyAssignedToUser(
        RuntimeUserContext user,
        RuntimePermissionSnapshot snapshot,
        RuntimeWorkflowTask task) =>
        task.AssignedUserId == user.UserId && HasDirectAssignmentRequiredRole(snapshot, task);

    private static bool HasDirectAssignmentRequiredRole(
        RuntimePermissionSnapshot snapshot,
        RuntimeWorkflowTask task)
    {
        var requiredRole = ReadMetadataString(task.Metadata, "requiredRole");
        return !string.IsNullOrWhiteSpace(requiredRole) &&
               snapshot.EffectiveRoleIds.Contains(requiredRole, StringComparer.OrdinalIgnoreCase);
    }

}
