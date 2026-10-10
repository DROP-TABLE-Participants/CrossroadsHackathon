using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases.Scheduling;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeWorkflowStore : IRuntimeWorkflowService
{
    private static readonly Regex IdentifierPattern = new("^[a-z][a-z0-9_]{1,62}$", RegexOptions.Compiled);
    private const int MaxWorkflowStartDeadlockAttempts = 3;
    private readonly ITenantDatabaseConnectionFactory connectionFactory;
    private readonly RuntimePermissionService permissions;
    private readonly TimeProvider timeProvider;

    public RuntimeWorkflowStore(ITenantDatabaseConnectionFactory connectionFactory)
        : this(connectionFactory, new RuntimePermissionService(connectionFactory), TimeProvider.System)
    {
    }

    public RuntimeWorkflowStore(
        ITenantDatabaseConnectionFactory connectionFactory,
        RuntimePermissionService permissions,
        TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory;
        this.permissions = permissions;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }


    public async Task<IReadOnlyList<RuntimeWorkflowDefinition>> ListWorkflowsAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        return await LoadWorkflowsAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<RuntimeRecordAction>> ListRecordActionsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        if (!permissions.IsAllowed(permissionSnapshot, "read", entity) ||
            !permissions.IsAllowed(permissionSnapshot, "start_workflow", entity))
        {
            return [];
        }

        var record = await ReadRecordByIdAsync(connection, entity, recordId, cancellationToken);
        if (record is null)
        {
            return [];
        }

        if (!await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction: null,
                permissionSnapshot,
                user,
                "read",
                entity,
                record,
                cancellationToken) ||
            !await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction: null,
                permissionSnapshot,
                user,
                "start_workflow",
                entity,
                record,
                cancellationToken))
        {
            return [];
        }

        var workflows = await LoadWorkflowsForEntityAsync(connection, entity.Id, cancellationToken);
        var actions = new List<RuntimeRecordAction>();
        foreach (var workflow in workflows.Where(workflow => workflow.Trigger is not null))
        {
            if (!CanStartWorkflowForRecord(workflow, entity, record))
            {
                continue;
            }

            var hasActiveInstance = await HasActiveInstanceAsync(connection, workflow.Id, entity.Id, recordId, cancellationToken);
            if (hasActiveInstance)
            {
                continue;
            }

            actions.Add(new RuntimeRecordAction(
                workflow.Trigger!.Action,
                ToActionLabel(workflow.Trigger.Action),
                workflow.Id));
        }

        return actions;
    }

    public async Task<bool> HasActionAsync(
        ResolvedTenantEnvironment tenant,
        string entityKey,
        string actionKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        return (await LoadWorkflowsForEntityAsync(connection, entity.Id, cancellationToken))
            .Any(candidate => string.Equals(candidate.Trigger?.Action, actionKey, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<RuntimeWorkflowStartResult> StartActionAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        string actionKey,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxWorkflowStartDeadlockAttempts; attempt++)
        {
            try
            {
                return await StartActionOnceAsync(
                    tenant,
                    user,
                    entityKey,
                    recordId,
                    actionKey,
                    cancellationToken);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.DeadlockDetected)
            {
                if (attempt == MaxWorkflowStartDeadlockAttempts - 1)
                {
                    throw new RuntimeWorkflowConflictException(
                        "Workflow start conflicted with another concurrent operation.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken);
            }
        }

        throw new RuntimeWorkflowConflictException(
            "Workflow start conflicted with another concurrent operation.");
    }

    private async Task<RuntimeWorkflowStartResult> StartActionOnceAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        string actionKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var workflow = (await LoadWorkflowsForEntityAsync(connection, entity.Id, cancellationToken))
            .FirstOrDefault(candidate => string.Equals(candidate.Trigger?.Action, actionKey, StringComparison.OrdinalIgnoreCase));
        if (workflow is null)
        {
            throw new RuntimeWorkflowNotFoundException(actionKey);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var permissionSnapshot = await permissions.CreateSnapshotAsync(
            connection,
            transaction,
            user,
            cancellationToken);
        if (!permissions.IsAllowed(permissionSnapshot, "read", entity))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowPermissionException("Record read permission is required to start a workflow.");
        }

        if (!permissions.IsAllowed(permissionSnapshot, "start_workflow", entity))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowPermissionException("Workflow start permission denied.");
        }

        var record = await ReadRecordByIdAsync(
            connection,
            entity,
            recordId,
            cancellationToken,
            transaction,
            forUpdate: true);
        if (record is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowRecordNotFoundException(entityKey, recordId);
        }

        if (!await permissions.IsCandidateRecordAllowedAsync(
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
            throw new RuntimeWorkflowRecordNotFoundException(entityKey, recordId);
        }

        if (!await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                permissionSnapshot,
                user,
                "start_workflow",
                entity,
                record,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowRecordStartPermissionException(
                "Workflow start is not allowed for this record.");
        }

        if (!CanStartWorkflowForRecord(workflow, entity, record))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowActionNotAvailableException(
                actionKey,
                "Workflow action is not available for the current record state.");
        }

        if (await HasActiveInstanceAsync(connection, workflow.Id, entity.Id, recordId, cancellationToken, transaction))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowActionNotAvailableException(
                actionKey,
                "A workflow instance is already active for this record.");
        }

        await EvaluateStartGuardsAsync(
            connection,
            transaction,
            workflow,
            entity,
            record,
            cancellationToken);

        var instance = await InsertInstanceAsync(connection, transaction, workflow, entity, recordId, user, cancellationToken);
        await InsertWorkflowEventAsync(
            connection,
            transaction,
            instance.Id,
            null,
            "workflow.started",
            $"Started {workflow.Name}.",
            new Dictionary<string, object?>
            {
                ["workflowId"] = workflow.Id,
                ["entity"] = entity.Key,
                ["recordId"] = recordId,
                ["action"] = actionKey
            },
            cancellationToken);
        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user,
            "workflow.started",
            $"Started {workflow.Name}.",
            new Dictionary<string, object?>
            {
                ["workflowId"] = workflow.Id,
                ["entity"] = entity.Key,
                ["recordId"] = recordId,
                ["action"] = actionKey,
                ["workflowInstanceId"] = instance.Id
            },
            cancellationToken);

        var execution = await ExecuteStepsAsync(
            connection,
            transaction,
            tenant,
            user,
            workflow,
            entity,
            recordId,
            instance.Id,
            workflow.Steps,
            0,
            null,
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
                    ["recordId"] = recordId
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
                    ["recordId"] = recordId,
                    ["workflowInstanceId"] = instance.Id
                },
                cancellationToken);
        }

        var finalRecord = await ReadRecordByIdAsync(
            connection,
            entity,
            recordId,
            cancellationToken,
            transaction,
            forUpdate: true);
        if (finalRecord is null ||
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
                "start_workflow",
                entity,
                finalRecord,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new RuntimeWorkflowPermissionException(
                "Workflow start would move the record outside the permitted scope.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new RuntimeWorkflowStartResult(instance, execution.PendingTask);
    }

    public async Task<IReadOnlyList<RuntimeWorkflowInstance>> ListInstancesAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var instances = new List<RuntimeWorkflowInstance>();
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            user,
            "wi",
            "workflow_instance_scope",
            cancellationToken);
        if (visibility.DeniesAllRows)
        {
            return [];
        }

        var canSeeAll = HasWorkflowAdminAuthority(permissionSnapshot);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select distinct wi.id, wi.workflow_id, wi.workflow_name, wi.entity_id, wi.entity_key, wi.record_id, wi.status, wi.current_step_id, wi.started_by, wi.completed_at, wi.metadata, wi.created_at, wi.updated_at
            from _runtime.workflow_instances wi
            left join _runtime.workflow_tasks t on t.instance_id = wi.id
            where ({(canSeeAll ? "true" : "wi.started_by = @userId or t.assigned_user_id = @userId or lower(t.assigned_role) = any(@roles)")})
              and ({visibility.Sql})
            order by wi.id desc
            limit 100
            """;
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("userId", user.UserId);
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
        }

        visibility.AddParameters(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            instances.Add(ReadInstance(reader));
        }

        return instances;
    }

    public async Task<IReadOnlyList<RuntimeWorkflowInstance>> ListRecordInstancesAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var instances = new List<RuntimeWorkflowInstance>();
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            user,
            "wi",
            "record_workflow_instance_scope",
            cancellationToken);
        if (visibility.DeniesAllRows)
        {
            return [];
        }

        var canSeeAll = HasWorkflowAdminAuthority(permissionSnapshot);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select distinct wi.id, wi.workflow_id, wi.workflow_name, wi.entity_id, wi.entity_key, wi.record_id, wi.status, wi.current_step_id, wi.started_by, wi.completed_at, wi.metadata, wi.created_at, wi.updated_at
            from _runtime.workflow_instances wi
            left join _runtime.workflow_tasks t on t.instance_id = wi.id
            where wi.entity_id = @entityId
              and wi.record_id = @recordId
              and ({(canSeeAll ? "true" : "wi.started_by = @userId or t.assigned_user_id = @userId or lower(t.assigned_role) = any(@roles)")})
              and ({visibility.Sql})
            order by wi.id desc
            limit @limit
            """;
        command.Parameters.AddWithValue("entityId", entity.Id);
        command.Parameters.AddWithValue("recordId", recordId);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("userId", user.UserId);
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
        }

        visibility.AddParameters(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            instances.Add(ReadInstance(reader));
        }

        return instances;
    }

    public async Task<RuntimeWorkflowInstanceDetail?> GetInstanceAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        long instanceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var instance = await LoadInstanceAsync(connection, instanceId, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var entity = await LoadEntityByIdAsync(connection, instance.EntityId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (!permissions.IsAllowed(permissionSnapshot, "read_task", entity) ||
            !permissions.IsAllowed(permissionSnapshot, "read", entity))
        {
            throw new RuntimeWorkflowPermissionException("Workflow instance read permission denied.");
        }

        var record = await ReadRecordByIdAsync(connection, entity, instance.RecordId, cancellationToken);
        if (record is null ||
            !await CanViewWorkflowRecordAsync(
                connection,
                transaction: null,
                permissionSnapshot,
                user,
                entity,
                record,
                cancellationToken))
        {
            return null;
        }

        var tasks = await LoadTasksForInstanceAsync(connection, instanceId, cancellationToken);
        if (!HasWorkflowAdminAuthority(permissionSnapshot) &&
            instance.StartedBy != user.UserId &&
            !tasks.Any(task => CanAccessTask(user, permissionSnapshot, task)))
        {
            throw new RuntimeWorkflowPermissionException("Workflow instance is not visible to this user.");
        }

        var events = await LoadEventsForInstanceAsync(connection, instanceId, cancellationToken);
        return new RuntimeWorkflowInstanceDetail(instance, tasks, events);
    }

    public async Task<IReadOnlyList<RuntimeWorkflowTask>> ListTasksAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        bool includeCompleted = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var tasks = new List<RuntimeWorkflowTask>();
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var directAssignmentVisibility = await BuildDirectAssignedTaskVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            "wt",
            "workflow_direct_task_scope",
            cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            user,
            "wt",
            "workflow_task_scope",
            cancellationToken);
        var canSeeAll = HasWorkflowAdminAuthority(permissionSnapshot);
        if (!canSeeAll && visibility.DeniesAllRows && directAssignmentVisibility.DeniesAllRows)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var statusFilter = includeCompleted ? string.Empty : "and wt.status = 'pending'";
        var accessFilter = canSeeAll
            ? "true"
            : $"""
              (wt.assigned_user_id = @userId
               and lower(coalesce(wt.metadata->>'requiredRole', '')) = any(@roles)
               and ({directAssignmentVisibility.Sql}))
              or
              (wt.assigned_user_id is null
               and lower(wt.assigned_role) = any(@roles)
               and ({visibility.Sql}))
              """;
        command.CommandText = $"""
            select wt.id, wt.instance_id, wt.workflow_id, wt.step_id, wt.label, wt.entity_id, wt.record_id, wt.status, wt.assigned_role, wt.assigned_user_id, wt.decision_by, wt.decision_at, wt.reason, wt.metadata, wt.created_at, wt.updated_at
            from _runtime.workflow_tasks wt
            where ({accessFilter})
              {statusFilter}
            order by wt.id desc
            limit 100
            """;
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("userId", user.UserId);
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
            directAssignmentVisibility.AddParameters(command);
            visibility.AddParameters(command);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    public async Task<RuntimeWorkflowTask?> GetTaskAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        long taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var task = await LoadTaskAsync(connection, taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var entity = await LoadEntityByIdAsync(connection, task.EntityId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (!permissions.IsAllowed(permissionSnapshot, "read_task", entity))
        {
            throw new RuntimeWorkflowPermissionException("Workflow task read permission denied.");
        }

        var directlyAssigned = IsTaskDirectlyAssignedToUser(user, permissionSnapshot, task);
        if (!directlyAssigned)
        {
            if (!permissions.IsAllowed(permissionSnapshot, "read", entity))
            {
                throw new RuntimeWorkflowPermissionException("Workflow task read permission denied.");
            }

            var record = await ReadRecordByIdAsync(connection, entity, task.RecordId, cancellationToken);
            if (record is null ||
                !await CanViewWorkflowRecordAsync(
                    connection,
                    transaction: null,
                    permissionSnapshot,
                    user,
                    entity,
                    record,
                    cancellationToken))
            {
                return null;
            }

            if (!HasWorkflowAdminAuthority(permissionSnapshot) &&
                !CanAccessTask(user, permissionSnapshot, task))
            {
                throw new RuntimeWorkflowPermissionException("Workflow task is not visible to this user.");
            }
        }

        return task;
    }

    public async Task<RuntimeWorkflowTaskRecordDetail?> GetTaskRecordAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        long taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var task = await LoadTaskAsync(connection, taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var entity = await LoadEntityByIdAsync(connection, task.EntityId, cancellationToken);
        if (entity is null || !permissions.IsAllowed(permissionSnapshot, "read_task", entity))
        {
            throw new RuntimeWorkflowPermissionException("Workflow task evidence read permission denied.");
        }

        var directlyAssigned = IsTaskDirectlyAssignedToUser(user, permissionSnapshot, task);
        RuntimeEntityDefinition readableEntity;
        if (directlyAssigned)
        {
            var hasEntityRead = permissions.IsAllowed(permissionSnapshot, "read", entity);
            readableEntity = hasEntityRead
                ? permissions.FilterReadableEntity(permissionSnapshot, entity)
                : permissions.FilterExplicitTaskReadableEntity(permissionSnapshot, entity);
            if (!hasEntityRead && ReadMetadataStringArray(task.Metadata, "reviewFields").Count == 0)
            {
                throw new RuntimeWorkflowPermissionException(
                    "Directly assigned workflow task has no closed review-fields contract.");
            }
        }
        else
        {
            if (!permissions.IsAllowed(permissionSnapshot, "read", entity) ||
                !HasWorkflowAdminAuthority(permissionSnapshot) &&
                !CanAccessTask(user, permissionSnapshot, task))
            {
                throw new RuntimeWorkflowPermissionException("Workflow task evidence read permission denied.");
            }

            readableEntity = permissions.FilterReadableEntity(permissionSnapshot, entity);
        }

        var reviewFields = ReadMetadataStringArray(task.Metadata, "reviewFields");
        IReadOnlyList<(string Key, RuntimeFieldDefinition Field)> readableFields;
        if (reviewFields.Count > 0)
        {
            readableFields = reviewFields
                .Select(reviewField => (
                    Key: reviewField,
                    Field: readableEntity.Fields.SingleOrDefault(field =>
                        RuntimePermissionService.FieldMatchesReference(field, reviewField))))
                .Where(item => item.Field is not null)
                .Select(item => (item.Key, item.Field!))
                .ToArray();
            if (readableFields.Count != reviewFields.Count)
            {
                throw new RuntimeWorkflowPermissionException(
                    "Workflow task review fields are not all readable by the assigned principal.");
            }
        }
        else
        {
            readableFields = readableEntity.Fields
                .Select(field => (RuntimeFieldKey(entity, field), field))
                .ToArray();
        }

        var record = await ReadRecordByIdAsync(connection, entity, task.RecordId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        if (!directlyAssigned &&
            !await CanViewWorkflowRecordAsync(
                connection,
                transaction: null,
                permissionSnapshot,
                user,
                entity,
                record,
                cancellationToken))
        {
            return null;
        }

        return new RuntimeWorkflowTaskRecordDetail(
            task,
            entity.Id,
            entity.DisplayName,
            task.RecordId,
            readableFields.Select(item => new RuntimeWorkflowTaskEvidenceField(
                item.Key,
                item.Field.DisplayName,
                item.Field.FieldType,
                record.GetValueOrDefault(item.Field.ColumnName))).ToArray());
    }

    public async Task<RuntimeWorkflowTaskResult> ApproveTaskAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        long taskId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return await CompleteTaskAsync(tenant, user, taskId, "approved", reason, cancellationToken);
    }

    public async Task<RuntimeWorkflowTaskResult> RejectTaskAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        long taskId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return await CompleteTaskAsync(tenant, user, taskId, "rejected", reason, cancellationToken);
    }

    public async Task<long> CountPendingTasksAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var directAssignmentVisibility = await BuildDirectAssignedTaskVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            "pt",
            "pending_direct_task_scope",
            cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            user,
            "pt",
            "pending_task_scope",
            cancellationToken);
        var canSeeAll = HasWorkflowAdminAuthority(permissionSnapshot);
        if (!canSeeAll && visibility.DeniesAllRows && directAssignmentVisibility.DeniesAllRows)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        var accessFilter = canSeeAll
            ? "true"
            : $"""
              (pt.assigned_user_id = @userId
               and lower(coalesce(pt.metadata->>'requiredRole', '')) = any(@roles)
               and ({directAssignmentVisibility.Sql}))
              or
              (pt.assigned_user_id is null
               and lower(pt.assigned_role) = any(@roles)
               and ({visibility.Sql}))
              """;
        command.CommandText = $"""
            select count(*)
            from _runtime.workflow_tasks pt
            where pt.status = 'pending'
              and ({accessFilter})
            """;
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("userId", user.UserId);
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
            directAssignmentVisibility.AddParameters(command);
            visibility.AddParameters(command);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<long> CountUnreadNotificationsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var directAssignmentVisibility = await BuildDirectAssignedTaskVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            "nt",
            "unread_notification_direct_task_scope",
            cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            user,
            "ni",
            "unread_notification_scope",
            cancellationToken);
        var canSeeAll = permissionSnapshot.HasSystemOwnerAuthority;
        if (!canSeeAll && visibility.DeniesAllRows && directAssignmentVisibility.DeniesAllRows)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        var accessFilter = canSeeAll
            ? "true"
            : $"""
              (n.recipient_user_id = @userId
               and exists (
                 select 1
                 from _runtime.workflow_tasks nt
                 where nt.id = n.task_id
                   and nt.assigned_user_id = @userId
                   and lower(coalesce(nt.metadata->>'requiredRole', '')) = any(@roles)
                   and ({directAssignmentVisibility.Sql})))
              or
              (n.recipient_user_id is null
               and lower(n.recipient_role) = any(@roles)
               and ({visibility.Sql}))
              """;
        command.CommandText = $"""
            select count(*)
            from _runtime.notification_outbox n
            join _runtime.workflow_instances ni on ni.id = n.instance_id
            where n.status = 'unread'
              and ({accessFilter})
            """;
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("userId", user.UserId);
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
            directAssignmentVisibility.AddParameters(command);
            visibility.AddParameters(command);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<RuntimeNotification>> ListNotificationsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        bool includeAll = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var notifications = new List<RuntimeNotification>();
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var directAssignmentVisibility = await BuildDirectAssignedTaskVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            "nt",
            "notification_direct_task_scope",
            cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            user,
            "ni",
            "notification_scope",
            cancellationToken);
        var canSeeAll = permissionSnapshot.HasSystemOwnerAuthority ||
                        includeAll && HasWorkflowAdminAuthority(permissionSnapshot);
        if (!canSeeAll && visibility.DeniesAllRows && directAssignmentVisibility.DeniesAllRows)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var accessFilter = canSeeAll
            ? "true"
            : $"""
              (n.recipient_user_id = @userId
               and exists (
                 select 1
                 from _runtime.workflow_tasks nt
                 where nt.id = n.task_id
                   and nt.assigned_user_id = @userId
                   and lower(coalesce(nt.metadata->>'requiredRole', '')) = any(@roles)
                   and ({directAssignmentVisibility.Sql})))
              or
              (n.recipient_user_id is null
               and lower(n.recipient_role) = any(@roles)
               and ({visibility.Sql}))
              """;
        command.CommandText = $"""
            select n.id, n.instance_id, n.task_id, n.channel, n.notification_type, n.recipient_role, n.recipient_user_id,
                   coalesce(nullif(n.title, ''), n.subject) as title,
                   coalesce(nullif(n.message, ''), n.subject) as message,
                   n.link,
                   n.subject,
                   n.payload,
                   n.status,
                   n.read_at,
                   n.read_by,
                   n.created_at
            from _runtime.notification_outbox n
            join _runtime.workflow_instances ni on ni.id = n.instance_id
            where ({accessFilter})
            order by n.id desc
            limit 100
            """;
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("userId", user.UserId);
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
            directAssignmentVisibility.AddParameters(command);
            visibility.AddParameters(command);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            notifications.Add(ReadNotification(reader));
        }

        return notifications;
    }

    public async Task<bool> MarkNotificationReadAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        long notificationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var permissionSnapshot = await permissions.CreateSnapshotAsync(
            connection,
            transaction,
            user,
            cancellationToken);
        var directAssignmentVisibility = await BuildDirectAssignedTaskVisibilityPredicateAsync(
            connection,
            transaction,
            permissionSnapshot,
            "nt",
            "read_notification_direct_task_scope",
            cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            "ni",
            "read_notification_scope",
            cancellationToken);
        var canSeeAll = permissionSnapshot.HasSystemOwnerAuthority;
        if (!canSeeAll && visibility.DeniesAllRows && directAssignmentVisibility.DeniesAllRows)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var accessFilter = canSeeAll
            ? "true"
            : $"""
              (n.recipient_user_id = @userId
               and exists (
                 select 1
                 from _runtime.workflow_tasks nt
                 where nt.id = n.task_id
                   and nt.assigned_user_id = @userId
                   and lower(coalesce(nt.metadata->>'requiredRole', '')) = any(@roles)
                   and ({directAssignmentVisibility.Sql})))
              or
              (n.recipient_user_id is null
               and lower(n.recipient_role) = any(@roles)
               and ({visibility.Sql}))
              """;
        await using var command = new NpgsqlCommand(
            $"""
            update _runtime.notification_outbox n
            set status = 'read',
                read_at = coalesce(n.read_at, now()),
                read_by = coalesce(n.read_by, @userId)
            from _runtime.workflow_instances ni
            where n.id = @id
              and n.instance_id = ni.id
              and n.status <> 'read'
              and ({accessFilter})
            returning n.id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", notificationId);
        command.Parameters.AddWithValue("userId", user.UserId);
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
            directAssignmentVisibility.AddParameters(command);
            visibility.AddParameters(command);
        }
        var updated = await command.ExecuteScalarAsync(cancellationToken);
        if (updated is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user,
            "notification.read",
            "Runtime notification read.",
            new Dictionary<string, object?>
            {
                ["notificationId"] = notificationId
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<long> MarkAllNotificationsReadAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var permissionSnapshot = await permissions.CreateSnapshotAsync(
            connection,
            transaction,
            user,
            cancellationToken);
        var directAssignmentVisibility = await BuildDirectAssignedTaskVisibilityPredicateAsync(
            connection,
            transaction,
            permissionSnapshot,
            "nt",
            "read_all_notification_direct_task_scope",
            cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            "ni",
            "read_all_notification_scope",
            cancellationToken);
        var canSeeAll = permissionSnapshot.HasSystemOwnerAuthority;
        if (!canSeeAll && visibility.DeniesAllRows && directAssignmentVisibility.DeniesAllRows)
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        var accessFilter = canSeeAll
            ? "true"
            : $"""
              (n.recipient_user_id = @userId
               and exists (
                 select 1
                 from _runtime.workflow_tasks nt
                 where nt.id = n.task_id
                   and nt.assigned_user_id = @userId
                   and lower(coalesce(nt.metadata->>'requiredRole', '')) = any(@roles)
                   and ({directAssignmentVisibility.Sql})))
              or
              (n.recipient_user_id is null
               and lower(n.recipient_role) = any(@roles)
               and ({visibility.Sql}))
              """;
        await using var command = new NpgsqlCommand(
            $"""
            update _runtime.notification_outbox n
            set status = 'read',
                read_at = coalesce(n.read_at, now()),
                read_by = coalesce(n.read_by, @userId)
            from _runtime.workflow_instances ni
            where n.instance_id = ni.id
              and n.status <> 'read'
              and ({accessFilter})
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("userId", user.UserId);
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
            directAssignmentVisibility.AddParameters(command);
            visibility.AddParameters(command);
        }
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated > 0)
        {
            await WriteAuditEventAsync(
                connection,
                transaction,
                tenant,
                user,
                "notification.read",
                "Runtime notifications read.",
                new Dictionary<string, object?>
                {
                    ["count"] = updated
                },
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<long> CountRunningInstancesAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            user,
            "ri",
            "running_instance_scope",
            cancellationToken);
        if (visibility.DeniesAllRows)
        {
            return 0;
        }

        var canSeeAll = HasWorkflowAdminAuthority(permissionSnapshot);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select count(distinct ri.id)
            from _runtime.workflow_instances ri
            left join _runtime.workflow_tasks t on t.instance_id = ri.id
            where ri.status in ('running', 'waiting')
              and ({(canSeeAll ? "true" : "ri.started_by = @userId or t.assigned_user_id = @userId or lower(t.assigned_role) = any(@roles)")})
              and ({visibility.Sql})
            """;
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("userId", user.UserId);
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
        }

        visibility.AddParameters(command);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<RuntimeWorkflowEvent>> ListRecentEventsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var events = new List<RuntimeWorkflowEvent>();
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var visibility = await BuildWorkflowRecordVisibilityPredicateAsync(
            connection,
            transaction: null,
            permissionSnapshot,
            user,
            "ei",
            "workflow_event_scope",
            cancellationToken);
        if (visibility.DeniesAllRows)
        {
            return [];
        }

        var canSeeAll = HasWorkflowAdminAuthority(permissionSnapshot);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select distinct e.id, e.instance_id, e.task_id, e.event_type, e.message, e.metadata, e.created_at
            from _runtime.workflow_events e
            join _runtime.workflow_instances ei on ei.id = e.instance_id
            left join _runtime.workflow_tasks t on t.instance_id = ei.id
            where ({(canSeeAll ? "true" : "ei.started_by = @userId or t.assigned_user_id = @userId or lower(t.assigned_role) = any(@roles)")})
              and ({visibility.Sql})
            order by e.id desc
            limit @limit
            """;
        if (!canSeeAll)
        {
            command.Parameters.AddWithValue("userId", user.UserId);
            command.Parameters.AddWithValue("roles", RoleArray(permissionSnapshot));
        }

        visibility.AddParameters(command);

        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 50));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

}
