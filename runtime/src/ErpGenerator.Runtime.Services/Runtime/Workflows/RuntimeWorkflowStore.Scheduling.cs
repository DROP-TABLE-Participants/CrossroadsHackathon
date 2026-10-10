using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Compilation.Hashing;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases.Scheduling;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeWorkflowStore
{
    internal async Task<RuntimeScheduledWorkflowExecutionResult> ExecuteScheduledAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeScheduledJob claimedJob,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claimedJob);
        var lease = claimedJob.Lease;
        var atUtc = timeProvider.GetUtcNow();
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var schedule = await LockAndLoadScheduledAuthorityAsync(
            connection,
            transaction,
            claimedJob,
            lease,
            atUtc,
            cancellationToken);
        var entity = await LoadEntityByIdAsync(connection, schedule.Entity, cancellationToken)
                     ?? throw new RuntimeWorkflowValidationException(
                         $"Scheduled entity '{schedule.Entity}' no longer exists.");
        var workflow = await LoadWorkflowAsync(connection, schedule.Workflow, cancellationToken)
                       ?? throw new RuntimeWorkflowNotFoundException(schedule.Workflow);
        if (workflow.Trigger is not null ||
            !workflow.Entity.Equals(schedule.Entity, StringComparison.Ordinal) ||
            !entity.Id.Equals(schedule.Entity, StringComparison.Ordinal))
        {
            throw new RuntimeWorkflowValidationException(
                "Scheduled workflow authority no longer matches one triggerless same-entity workflow.");
        }

        var record = await ReadRecordByIdAsync(
            connection,
            entity,
            claimedJob.RecordId,
            cancellationToken,
            transaction,
            forUpdate: true);
        var currentDueAt = record is null ? null : ReadCurrentScheduleDueAt(schedule, entity, record);
        var filtersMatch = record is not null && ScheduleFiltersMatch(schedule, entity, record);
        var eligible = record is not null && currentDueAt is not null && filtersMatch;
        if (!eligible || currentDueAt > atUtc)
        {
            await ReleaseChangedScheduleAuthorityAsync(
                connection,
                transaction,
                claimedJob,
                lease,
                currentDueAt,
                eligible,
                atUtc,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new RuntimeScheduleLeaseLostException(
                eligible
                    ? "The scheduled record due time moved into the future before execution."
                    : record is null
                        ? "The scheduled record no longer exists."
                        : currentDueAt is null
                            ? "The scheduled record no longer has a due value."
                            : "The scheduled record no longer matches its exact filters.");
        }

        if (!CanStartWorkflowForRecord(workflow, entity, record!))
        {
            throw new RuntimeWorkflowActionNotAvailableException(
                schedule.Workflow,
                "Scheduled workflow is not available for the current record state.");
        }

        await EvaluateStartGuardsAsync(
            connection,
            transaction,
            workflow,
            entity,
            record!,
            cancellationToken);

        var instance = await InsertScheduledInstanceAsync(
            connection,
            transaction,
            workflow,
            entity,
            claimedJob,
            schedule,
            cancellationToken);
        await InsertWorkflowEventAsync(
            connection,
            transaction,
            instance.Id,
            null,
            "workflow.started",
            $"Started {workflow.Name} from schedule {schedule.Id}.",
            ScheduledEventMetadata(schedule, claimedJob, workflow, entity, instance.Id),
            cancellationToken);
        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user: null,
            "workflow.started",
            $"Started {workflow.Name} from schedule {schedule.Id}.",
            ScheduledEventMetadata(schedule, claimedJob, workflow, entity, instance.Id),
            cancellationToken);

        var execution = await ExecuteStepsAsync(
            connection,
            transaction,
            tenant,
            user: null,
            workflow,
            entity,
            claimedJob.RecordId,
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
                ScheduledEventMetadata(schedule, claimedJob, workflow, entity, instance.Id),
                cancellationToken);
            await WriteAuditEventAsync(
                connection,
                transaction,
                tenant,
                user: null,
                "workflow.completed",
                $"Completed {workflow.Name}.",
                ScheduledEventMetadata(schedule, claimedJob, workflow, entity, instance.Id),
                cancellationToken);
        }

        await CompleteScheduledJobInTransactionAsync(
            connection,
            transaction,
            claimedJob,
            lease,
            instance.Id,
            atUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RuntimeScheduledWorkflowExecutionResult(instance.Id);
    }

    private static async Task<ScheduleDefinition> LockAndLoadScheduledAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScheduledJob job,
        RuntimeScheduleLease lease,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select schedules.id, schedules.entity_id, schedules.due_field,
                   schedules.workflow_id, schedules.definition_hash, schedules.metadata,
                   jobs.entity_id, jobs.record_id
            from _runtime.scheduled_jobs jobs
            join _runtime.schedule_definitions schedules
              on schedules.id = jobs.schedule_id
             and schedules.definition_hash = jobs.schedule_definition_hash
            join _runtime.active_bundle active
              on active.bundle_id = schedules.bundle_id
             and active.bundle_version = schedules.bundle_version
             and active.bundle_hash = schedules.bundle_hash
            where jobs.id = @jobId
              and jobs.status = 'running'
              and jobs.schedule_definition_hash = @definitionHash
              and jobs.lease_id = @leaseId
              and jobs.leased_by = @workerId
              and jobs.lease_expires_at > @atUtc
            for update of jobs, schedules, active
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("jobId", job.Id);
        command.Parameters.AddWithValue("definitionHash", lease.ScheduleDefinitionHash);
        command.Parameters.AddWithValue("leaseId", lease.LeaseId);
        command.Parameters.AddWithValue("workerId", lease.WorkerId);
        command.Parameters.AddWithValue("atUtc", atUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new RuntimeScheduleLeaseLostException(
                "The scheduled workflow lease or active definition is no longer authoritative.");
        }

        var persistedId = reader.GetString(0);
        var persistedEntity = reader.GetString(1);
        var persistedDueField = reader.GetString(2);
        var persistedWorkflow = reader.GetString(3);
        var persistedHash = reader.GetString(4);
        var definition = JsonSerializer.Deserialize<ScheduleDefinition>(
                             reader.GetString(5),
                             new JsonSerializerOptions(JsonSerializerDefaults.Web))
                         ?? throw new RuntimeWorkflowValidationException(
                             "Scheduled workflow metadata is missing.");
        var computedHash = ScheduleDefinitionHashCalculator.Compute(
            definition.Id,
            definition.Entity,
            definition.DueField,
            definition.Filters,
            definition.Workflow);
        if (!definition.Id.Equals(persistedId, StringComparison.Ordinal) ||
            !definition.Entity.Equals(persistedEntity, StringComparison.Ordinal) ||
            !definition.DueField.Equals(persistedDueField, StringComparison.Ordinal) ||
            !definition.Workflow.Equals(persistedWorkflow, StringComparison.Ordinal) ||
            !definition.DefinitionHash.Equals(persistedHash, StringComparison.Ordinal) ||
            !computedHash.Equals(persistedHash, StringComparison.Ordinal) ||
            !job.ScheduleId.Equals(persistedId, StringComparison.Ordinal) ||
            !job.EntityId.Equals(persistedEntity, StringComparison.Ordinal) ||
            !job.EntityId.Equals(reader.GetString(6), StringComparison.Ordinal) ||
            job.RecordId != reader.GetInt64(7))
        {
            throw new RuntimeScheduleLeaseLostException(
                "The scheduled workflow definition failed its exact compiled-lineage check.");
        }

        return definition;
    }

    private static async Task<RuntimeWorkflowInstance> InsertScheduledInstanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeWorkflowDefinition workflow,
        RuntimeEntityDefinition entity,
        RuntimeScheduledJob job,
        ScheduleDefinition schedule,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.workflow_instances (
              workflow_id, workflow_name, entity_id, entity_key, record_id, status,
              started_by, trigger_kind, trigger_key, scheduler_job_id, metadata)
            values (
              @workflowId, @workflowName, @entityId, @entityKey, @recordId, 'running',
              null, 'schedule', @scheduleId, @schedulerJobId, @metadata)
            returning id, workflow_id, workflow_name, entity_id, entity_key, record_id,
                      status, current_step_id, started_by, completed_at, metadata,
                      created_at, updated_at
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("workflowId", workflow.Id);
        command.Parameters.AddWithValue("workflowName", workflow.Name);
        command.Parameters.AddWithValue("entityId", entity.Id);
        command.Parameters.AddWithValue("entityKey", entity.Key);
        command.Parameters.AddWithValue("recordId", job.RecordId);
        command.Parameters.AddWithValue("scheduleId", schedule.Id);
        command.Parameters.AddWithValue("schedulerJobId", job.Id);
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["triggerKind"] = "schedule",
                ["triggerKey"] = schedule.Id,
                ["schedulerJobId"] = job.Id,
                ["scheduleDefinitionHash"] = schedule.DefinitionHash
            });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Scheduled workflow instance insert did not return a row.");
        }

        return ReadInstance(reader);
    }

    private static async Task CompleteScheduledJobInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScheduledJob job,
        RuntimeScheduleLease lease,
        long workflowInstanceId,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update _runtime.scheduled_jobs
            set status = 'completed',
                workflow_instance_id = @workflowInstanceId,
                lease_id = null,
                leased_by = null,
                lease_expires_at = null,
                last_error_code = null,
                completed_at = @atUtc,
                updated_at = @atUtc
            where id = @jobId
              and status = 'running'
              and schedule_definition_hash = @definitionHash
              and lease_id = @leaseId
              and leased_by = @workerId
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("workflowInstanceId", workflowInstanceId);
        command.Parameters.AddWithValue("atUtc", atUtc);
        command.Parameters.AddWithValue("jobId", job.Id);
        command.Parameters.AddWithValue("definitionHash", lease.ScheduleDefinitionHash);
        command.Parameters.AddWithValue("leaseId", lease.LeaseId);
        command.Parameters.AddWithValue("workerId", lease.WorkerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new RuntimeScheduleLeaseLostException(
                "The scheduled workflow job lost its exact lease before completion.");
        }
    }

    private static async Task ReleaseChangedScheduleAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScheduledJob job,
        RuntimeScheduleLease lease,
        DateTimeOffset? currentDueAt,
        bool otherwiseEligible,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        var status = otherwiseEligible ? RuntimeScheduleJobStatuses.Ready : RuntimeScheduleJobStatuses.Cancelled;
        var availableAt = otherwiseEligible && currentDueAt is not null ? currentDueAt.Value : atUtc;
        await using var command = new NpgsqlCommand(
            """
            update _runtime.scheduled_jobs
            set status = @status,
                due_at = coalesce(@dueAt, due_at),
                available_at = @availableAt,
                attempt = greatest(attempt - 1, 0),
                lease_id = null,
                leased_by = null,
                lease_expires_at = null,
                last_error_code = null,
                completed_at = case when @status = 'cancelled' then @atUtc else null end,
                updated_at = @atUtc
            where id = @jobId
              and status = 'running'
              and schedule_definition_hash = @definitionHash
              and lease_id = @leaseId
              and leased_by = @workerId
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("dueAt", (object?)currentDueAt ?? DBNull.Value);
        command.Parameters.AddWithValue("availableAt", availableAt);
        command.Parameters.AddWithValue("atUtc", atUtc);
        command.Parameters.AddWithValue("jobId", job.Id);
        command.Parameters.AddWithValue("definitionHash", lease.ScheduleDefinitionHash);
        command.Parameters.AddWithValue("leaseId", lease.LeaseId);
        command.Parameters.AddWithValue("workerId", lease.WorkerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new RuntimeScheduleLeaseLostException(
                "The changed scheduled record could not release its exact lease.");
        }
    }

    private static DateTimeOffset? ReadCurrentScheduleDueAt(
        ScheduleDefinition schedule,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> record)
    {
        var dueField = entity.Fields.SingleOrDefault(candidate =>
            RuntimeFieldMatchesCompiledId(entity, candidate, schedule.DueField));
        if (dueField is null ||
            !dueField.FieldType.Equals("datetime", StringComparison.Ordinal) ||
            dueField.Computed is not null ||
            !record.TryGetValue(dueField.ColumnName, out var raw) ||
            raw is null or DBNull)
        {
            return null;
        }

        return raw switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero),
            _ => DateTimeOffset.Parse(
                Convert.ToString(raw, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind)
        };
    }

    private static bool ScheduleFiltersMatch(
        ScheduleDefinition schedule,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> record)
    {
        foreach (var filter in schedule.Filters)
        {
            var field = entity.Fields.SingleOrDefault(candidate =>
                RuntimeFieldMatchesCompiledId(entity, candidate, filter.Field));
            if (field is null || field.Computed is not null ||
                !record.TryGetValue(field.ColumnName, out var raw))
            {
                return false;
            }

            var exists = raw is not null and not DBNull;
            var matches = filter.Op switch
            {
                "exists" => exists && filter.Value is null && filter.Values.Count == 0,
                "not_exists" => !exists && filter.Value is null && filter.Values.Count == 0,
                "eq" => exists && filter.Value is not null && filter.Values.Count == 0 &&
                        CompareScheduleLiteral(field, raw!, filter.Value) == 0,
                "neq" => exists && filter.Value is not null && filter.Values.Count == 0 &&
                         CompareScheduleLiteral(field, raw!, filter.Value) != 0,
                "gt" => exists && filter.Value is not null && filter.Values.Count == 0 &&
                        CompareScheduleLiteral(field, raw!, filter.Value) > 0,
                "gte" => exists && filter.Value is not null && filter.Values.Count == 0 &&
                         CompareScheduleLiteral(field, raw!, filter.Value) >= 0,
                "lt" => exists && filter.Value is not null && filter.Values.Count == 0 &&
                        CompareScheduleLiteral(field, raw!, filter.Value) < 0,
                "lte" => exists && filter.Value is not null && filter.Values.Count == 0 &&
                         CompareScheduleLiteral(field, raw!, filter.Value) <= 0,
                "in" => exists && filter.Value is null && filter.Values.Count > 0 &&
                        filter.Values.Any(value => CompareScheduleLiteral(field, raw!, value) == 0),
                "not_in" => exists && filter.Value is null && filter.Values.Count > 0 &&
                            filter.Values.All(value => CompareScheduleLiteral(field, raw!, value) != 0),
                _ => false
            };
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RuntimeFieldMatchesCompiledId(
        RuntimeEntityDefinition entity,
        RuntimeFieldDefinition field,
        string compiledFieldId) =>
        field.Id.Equals($"{entity.Id}.{compiledFieldId}", StringComparison.Ordinal);

    private static int CompareScheduleLiteral(
        RuntimeFieldDefinition field,
        object raw,
        TypedLiteralDefinition literal)
    {
        var expectedType = field.FieldType switch
        {
            "string" or "text" or "enum" => "string",
            "integer" or "relation" => "integer",
            "number" or "decimal" or "money" => "decimal",
            "boolean" => "boolean",
            "date" => "date",
            "datetime" => "datetime",
            _ => string.Empty
        };
        if (!literal.Type.Equals(expectedType, StringComparison.Ordinal))
        {
            throw new RuntimeWorkflowValidationException(
                $"Scheduled filter literal for '{field.Id}' does not match its exact field type.");
        }

        return literal.Type switch
        {
            "string" => string.CompareOrdinal(
                Convert.ToString(raw, CultureInfo.InvariantCulture),
                literal.Value),
            "integer" => Convert.ToInt64(raw, CultureInfo.InvariantCulture)
                .CompareTo(long.Parse(literal.Value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            "decimal" => Convert.ToDecimal(raw, CultureInfo.InvariantCulture)
                .CompareTo(decimal.Parse(literal.Value, NumberStyles.Number, CultureInfo.InvariantCulture)),
            "boolean" => Convert.ToBoolean(raw, CultureInfo.InvariantCulture)
                .CompareTo(bool.Parse(literal.Value)),
            "date" => ReadDate(raw).CompareTo(
                DateOnly.ParseExact(literal.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            "datetime" => ReadDateTimeOffset(raw).UtcDateTime.CompareTo(
                DateTimeOffset.Parse(
                    literal.Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind).UtcDateTime),
            _ => throw new RuntimeWorkflowValidationException(
                $"Scheduled filter literal type '{literal.Type}' is unsupported.")
        };
    }

    private static DateOnly ReadDate(object raw) => raw switch
    {
        DateOnly value => value,
        DateTime value => DateOnly.FromDateTime(value),
        _ => DateOnly.Parse(
            Convert.ToString(raw, CultureInfo.InvariantCulture)!,
            CultureInfo.InvariantCulture)
    };

    private static DateTimeOffset ReadDateTimeOffset(object raw) => raw switch
    {
        DateTimeOffset value => value,
        DateTime value => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero),
        _ => DateTimeOffset.Parse(
            Convert.ToString(raw, CultureInfo.InvariantCulture)!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind)
    };

    private static IReadOnlyDictionary<string, object?> ScheduledEventMetadata(
        ScheduleDefinition schedule,
        RuntimeScheduledJob job,
        RuntimeWorkflowDefinition workflow,
        RuntimeEntityDefinition entity,
        long workflowInstanceId) => new Dictionary<string, object?>
        {
            ["workflowId"] = workflow.Id,
            ["entity"] = entity.Key,
            ["recordId"] = job.RecordId,
            ["workflowInstanceId"] = workflowInstanceId,
            ["triggerKind"] = "schedule",
            ["triggerKey"] = schedule.Id,
            ["schedulerJobId"] = job.Id,
            ["scheduleDefinitionHash"] = schedule.DefinitionHash
        };
}

public sealed class RuntimeScheduledWorkflowExecutor(RuntimeWorkflowStore workflows)
    : IRuntimeScheduledWorkflowExecutor
{
    public Task<RuntimeScheduledWorkflowExecutionResult> ExecuteAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeScheduledJob job,
        CancellationToken cancellationToken = default) =>
        workflows.ExecuteScheduledAsync(tenant, job, cancellationToken);
}
