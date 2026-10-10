using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases.Scheduling;

public sealed class RuntimeScheduleStore
{
    internal const int AcceptanceAdvisoryLockNamespace = 1_886_545_253;
    private static readonly Regex IdentifierPattern = new("^[a-z][a-z0-9_]{1,62}$", RegexOptions.Compiled);
    private static readonly Regex ErrorCodePattern = new("^[a-z][a-z0-9_.]{1,118}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITenantDatabaseConnectionFactory connectionFactory;
    private readonly RuntimeScheduleOptions options;
    private readonly TimeProvider timeProvider;

    public RuntimeScheduleStore(
        ITenantDatabaseConnectionFactory connectionFactory,
        RuntimeScheduleOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory;
        this.options = options ?? new RuntimeScheduleOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<RuntimeScheduledJob>> ListJobsAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{JobSelect} order by id";
        var jobs = new List<RuntimeScheduledJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    internal async Task<NpgsqlConnection> OpenConnectionAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(
            tenant,
            TenantDatabaseConnectionPurpose.Scheduler));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    internal static async Task<bool> HasSchedulerSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select to_regclass('_runtime.schedule_definitions') is not null
               and to_regclass('_runtime.scheduled_jobs') is not null
               and to_regclass('_runtime.active_bundle') is not null
            """;
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    internal static async Task<bool> TryAcquireAcceptanceFenceAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"select pg_try_advisory_lock({AcceptanceAdvisoryLockNamespace}, hashtext(current_database()))";
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    internal static async Task ReleaseAcceptanceFenceAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"select pg_advisory_unlock({AcceptanceAdvisoryLockNamespace}, hashtext(current_database()))";
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch
        {
            // Closing the owning session is the authoritative release boundary.
        }
    }

    internal async Task<RuntimeScheduleRefreshResult> RefreshAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var bindings = await LoadActiveBindingsAsync(connection, transaction, cancellationToken);
        var materialized = 0;
        var cancelled = 0;
        foreach (var binding in bindings)
        {
            materialized += await MaterializeAsync(connection, transaction, binding, now, cancellationToken);
            cancelled += await CancelIneligibleAsync(connection, transaction, binding, now, cancellationToken);
        }

        var (reclaimed, exhausted) = await ReclaimExpiredAsync(
            connection,
            transaction,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RuntimeScheduleRefreshResult(materialized, cancelled, reclaimed, exhausted);
    }

    internal async Task<RuntimeScheduledJob?> ClaimNextAsync(
        NpgsqlConnection connection,
        string workerId,
        CancellationToken cancellationToken)
    {
        ValidateWorkerId(workerId);
        var now = timeProvider.GetUtcNow();
        var leaseId = Guid.NewGuid();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            with candidate as (
              select jobs.id
              from _runtime.scheduled_jobs jobs
              join _runtime.schedule_definitions schedules
                on schedules.id = jobs.schedule_id
               and schedules.definition_hash = jobs.schedule_definition_hash
              join _runtime.active_bundle active
                on active.bundle_id = schedules.bundle_id
               and active.bundle_version = schedules.bundle_version
               and active.bundle_hash = schedules.bundle_hash
              where jobs.status in ('ready', 'retry')
                and jobs.available_at <= @now
                and jobs.attempt < jobs.max_attempts
              order by jobs.available_at, jobs.due_at, jobs.id
              for update of jobs skip locked
              limit 1
            )
            update _runtime.scheduled_jobs jobs
            set status = 'running',
                attempt = jobs.attempt + 1,
                lease_id = @leaseId,
                leased_by = @workerId,
                lease_expires_at = @leaseExpiresAt,
                last_error_code = null,
                updated_at = @now
            from candidate
            where jobs.id = candidate.id
            returning {ReturningColumns("jobs")}
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("leaseId", leaseId);
        command.Parameters.AddWithValue("workerId", workerId);
        command.Parameters.AddWithValue("leaseExpiresAt", now + options.LeaseDuration);
        RuntimeScheduledJob? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                result = ReadJob(reader);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal async Task<bool> HeartbeatAsync(
        NpgsqlConnection connection,
        RuntimeScheduleLease lease,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            update _runtime.scheduled_jobs jobs
            set lease_expires_at = @leaseExpiresAt,
                updated_at = @now
            where {LeasePredicate("jobs")}
              and jobs.lease_expires_at > @now
              and exists (
                select 1
                from _runtime.schedule_definitions schedules
                join _runtime.active_bundle active
                  on active.bundle_id = schedules.bundle_id
                 and active.bundle_version = schedules.bundle_version
                 and active.bundle_hash = schedules.bundle_hash
                where schedules.id = jobs.schedule_id
                  and schedules.definition_hash = jobs.schedule_definition_hash)
            """;
        AddLeaseParameters(command, lease, now);
        command.Parameters.AddWithValue("leaseExpiresAt", now + options.LeaseDuration);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    internal async Task<bool> CompleteAsync(
        NpgsqlConnection connection,
        RuntimeScheduleLease lease,
        long workflowInstanceId,
        CancellationToken cancellationToken)
    {
        if (workflowInstanceId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workflowInstanceId));
        }

        var now = timeProvider.GetUtcNow();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            update _runtime.scheduled_jobs jobs
            set status = 'completed',
                workflow_instance_id = @workflowInstanceId,
                lease_id = null,
                leased_by = null,
                lease_expires_at = null,
                last_error_code = null,
                completed_at = @now,
                updated_at = @now
            where {LeasePredicate("jobs")}
              and jobs.lease_expires_at > @now
              and exists (
                select 1
                from _runtime.schedule_definitions schedules
                join _runtime.active_bundle active
                  on active.bundle_id = schedules.bundle_id
                 and active.bundle_version = schedules.bundle_version
                 and active.bundle_hash = schedules.bundle_hash
                where schedules.id = jobs.schedule_id
                  and schedules.definition_hash = jobs.schedule_definition_hash)
              and exists (
                select 1
                from _runtime.workflow_instances instances
                where instances.id = @workflowInstanceId
                  and instances.scheduler_job_id = jobs.id)
            """;
        AddLeaseParameters(command, lease, now);
        command.Parameters.AddWithValue("workflowInstanceId", workflowInstanceId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return true;
        }

        await using var idempotency = connection.CreateCommand();
        idempotency.CommandText = """
            select exists (
              select 1
              from _runtime.scheduled_jobs jobs
              join _runtime.workflow_instances instances
                on instances.id = jobs.workflow_instance_id
               and instances.scheduler_job_id = jobs.id
              where jobs.id = @jobId
                and jobs.schedule_definition_hash = @definitionHash
                and jobs.status = 'completed'
                and jobs.workflow_instance_id = @workflowInstanceId)
            """;
        idempotency.Parameters.AddWithValue("jobId", lease.JobId);
        idempotency.Parameters.AddWithValue("definitionHash", lease.ScheduleDefinitionHash);
        idempotency.Parameters.AddWithValue("workflowInstanceId", workflowInstanceId);
        return await idempotency.ExecuteScalarAsync(cancellationToken) is true;
    }

    internal async Task<string?> FailAsync(
        NpgsqlConnection connection,
        RuntimeScheduledJob job,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (!ErrorCodePattern.IsMatch(errorCode))
        {
            throw new ArgumentException("A stable lower-case error code is required.", nameof(errorCode));
        }

        var lease = job.Lease;
        var now = timeProvider.GetUtcNow();
        var exhausted = job.Attempt >= job.MaxAttempts;
        var status = exhausted ? RuntimeScheduleJobStatuses.Exhausted : RuntimeScheduleJobStatuses.Retry;
        var availableAt = exhausted ? now : now + RetryDelay(job.Attempt);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            update _runtime.scheduled_jobs jobs
            set status = @status,
                available_at = @availableAt,
                lease_id = null,
                leased_by = null,
                lease_expires_at = null,
                last_error_code = @errorCode,
                completed_at = case when @status = 'exhausted' then @now else null end,
                updated_at = @now
            where {LeasePredicate("jobs")}
              and jobs.lease_expires_at > @now
              and exists (
                select 1
                from _runtime.schedule_definitions schedules
                join _runtime.active_bundle active
                  on active.bundle_id = schedules.bundle_id
                 and active.bundle_version = schedules.bundle_version
                 and active.bundle_hash = schedules.bundle_hash
                where schedules.id = jobs.schedule_id
                  and schedules.definition_hash = jobs.schedule_definition_hash)
            """;
        AddLeaseParameters(command, lease, now);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("availableAt", availableAt);
        command.Parameters.AddWithValue("errorCode", errorCode);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? status : null;
    }

    private async Task<IReadOnlyList<RuntimeScheduleBinding>> LoadActiveBindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select schedules.id, schedules.entity_id, schedules.due_field,
                   schedules.workflow_id, schedules.definition_hash, schedules.metadata,
                   entities.metadata, workflows.metadata
            from _runtime.schedule_definitions schedules
            join _runtime.active_bundle active
              on active.bundle_id = schedules.bundle_id
             and active.bundle_version = schedules.bundle_version
             and active.bundle_hash = schedules.bundle_hash
            join _runtime.entity_definitions entities on entities.id = schedules.entity_id
            join _runtime.workflow_definitions workflows on workflows.id = schedules.workflow_id
            order by schedules.id
            for share of schedules, active, entities, workflows
            """,
            connection,
            transaction);
        var bindings = new List<RuntimeScheduleBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var definition = JsonSerializer.Deserialize<ScheduleDefinition>(reader.GetString(5), JsonOptions)
                             ?? throw new InvalidOperationException("Schedule metadata is missing.");
            var entity = JsonSerializer.Deserialize<EntityDefinition>(reader.GetString(6), JsonOptions)
                         ?? throw new InvalidOperationException("Schedule entity metadata is missing.");
            var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(reader.GetString(7), JsonOptions)
                           ?? throw new InvalidOperationException("Schedule workflow metadata is missing.");
            ValidateBinding(
                definition,
                entity,
                workflow,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4));
            bindings.Add(new RuntimeScheduleBinding(definition, entity));
        }

        return bindings;
    }

    private async Task<int> MaterializeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScheduleBinding binding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var parameters = new List<(string Name, object Value)>();
        var eligibility = BuildEligibilitySql(binding, "records", parameters);
        await using var command = new NpgsqlCommand(
            $"""
            insert into _runtime.scheduled_jobs as existing (
              schedule_id, schedule_definition_hash, entity_id, record_id, due_at,
              status, attempt, max_attempts, available_at, updated_at)
            select @scheduleId, @definitionHash, @entityId, records.id,
                   records.{Quote(binding.DueField.ColumnName)}, 'ready', 0, @maxAttempts,
                   records.{Quote(binding.DueField.ColumnName)}, @now
            from app.{Quote(binding.Entity.TableName)} records
            where {eligibility}
            on conflict (schedule_id, entity_id, record_id) do update set
              schedule_definition_hash = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null
                  then excluded.schedule_definition_hash
                else existing.schedule_definition_hash end,
              due_at = excluded.due_at,
              status = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null
                  then 'ready'
                else existing.status end,
              attempt = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null
                  then 0
                else existing.attempt end,
              max_attempts = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null
                  then excluded.max_attempts
                else existing.max_attempts end,
              available_at = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null
                  then excluded.due_at
                when existing.status = 'ready'
                  then excluded.due_at
                when existing.status = 'retry'
                  then greatest(existing.available_at, excluded.due_at)
                else existing.available_at end,
              lease_id = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null then null
                else existing.lease_id end,
              leased_by = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null then null
                else existing.leased_by end,
              lease_expires_at = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null then null
                else existing.lease_expires_at end,
              last_error_code = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null then null
                else existing.last_error_code end,
              completed_at = case
                when existing.status in ('superseded', 'cancelled') and existing.workflow_instance_id is null then null
                else existing.completed_at end,
              updated_at = excluded.updated_at
            where (existing.schedule_definition_hash = excluded.schedule_definition_hash
                   and existing.status in ('ready', 'retry'))
               or (existing.status = 'superseded'
                   and existing.workflow_instance_id is null
                   and existing.schedule_definition_hash <> excluded.schedule_definition_hash)
               or (existing.status = 'cancelled'
                   and existing.workflow_instance_id is null
                   and existing.schedule_definition_hash = excluded.schedule_definition_hash)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scheduleId", binding.Definition.Id);
        command.Parameters.AddWithValue("definitionHash", binding.Definition.DefinitionHash);
        command.Parameters.AddWithValue("entityId", binding.Definition.Entity);
        command.Parameters.AddWithValue("maxAttempts", options.MaxAttempts);
        command.Parameters.AddWithValue("now", now);
        AddDynamicParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> CancelIneligibleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScheduleBinding binding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var parameters = new List<(string Name, object Value)>();
        var eligibility = BuildEligibilitySql(binding, "records", parameters);
        await using var command = new NpgsqlCommand(
            $"""
            update _runtime.scheduled_jobs jobs
            set status = 'cancelled',
                last_error_code = null,
                completed_at = @now,
                updated_at = @now
            where jobs.schedule_id = @scheduleId
              and jobs.schedule_definition_hash = @definitionHash
              and jobs.entity_id = @entityId
              and jobs.status in ('ready', 'retry')
              and not exists (
                select 1
                from app.{Quote(binding.Entity.TableName)} records
                where records.id = jobs.record_id
                  and {eligibility})
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scheduleId", binding.Definition.Id);
        command.Parameters.AddWithValue("definitionHash", binding.Definition.DefinitionHash);
        command.Parameters.AddWithValue("entityId", binding.Definition.Entity);
        command.Parameters.AddWithValue("now", now);
        AddDynamicParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(int Reclaimed, int Exhausted)> ReclaimExpiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expired = new List<(long Id, int Attempt, int MaxAttempts)>();
        await using (var select = new NpgsqlCommand(
                         """
                         select id, attempt, max_attempts
                         from _runtime.scheduled_jobs
                         where status = 'running'
                           and lease_expires_at <= @now
                         order by lease_expires_at, id
                         for update skip locked
                         limit 1000
                         """,
                         connection,
                         transaction))
        {
            select.Parameters.AddWithValue("now", now);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                expired.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2)));
            }
        }

        var exhausted = 0;
        foreach (var job in expired)
        {
            var isExhausted = job.Attempt >= job.MaxAttempts;
            await using var update = new NpgsqlCommand(
                """
                update _runtime.scheduled_jobs
                set status = @status,
                    available_at = @availableAt,
                    lease_id = null,
                    leased_by = null,
                    lease_expires_at = null,
                    last_error_code = @errorCode,
                    completed_at = case when @status = 'exhausted' then @now else null end,
                    updated_at = @now
                where id = @id
                  and status = 'running'
                  and lease_expires_at <= @now
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue("id", job.Id);
            update.Parameters.AddWithValue("status", isExhausted ? RuntimeScheduleJobStatuses.Exhausted : RuntimeScheduleJobStatuses.Retry);
            update.Parameters.AddWithValue("availableAt", isExhausted ? now : now + RetryDelay(job.Attempt));
            update.Parameters.AddWithValue("errorCode", RuntimeScheduleErrorCodes.LeaseExpired);
            update.Parameters.AddWithValue("now", now);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1 && isExhausted)
            {
                exhausted++;
            }
        }

        return (expired.Count, exhausted);
    }

    private string BuildEligibilitySql(
        RuntimeScheduleBinding binding,
        string alias,
        List<(string Name, object Value)> parameters)
    {
        var clauses = new List<string>
        {
            $"{alias}.archived_at is null",
            $"{alias}.{Quote(binding.DueField.ColumnName)} is not null"
        };
        for (var filterIndex = 0; filterIndex < binding.Definition.Filters.Count; filterIndex++)
        {
            var filter = binding.Definition.Filters[filterIndex];
            var field = binding.Entity.Fields.Single(candidate =>
                candidate.Id.Equals(filter.Field, StringComparison.Ordinal));
            EnsureMaterializedField(field, $"schedule filter '{filter.Field}'");
            var column = $"{alias}.{Quote(field.ColumnName)}";
            switch (filter.Op)
            {
                case "eq":
                case "neq":
                case "gt":
                case "gte":
                case "lt":
                case "lte":
                    if (filter.Value is null || filter.Values.Count != 0)
                    {
                        throw new InvalidOperationException($"Schedule filter '{filter.Field}' has an invalid scalar operand.");
                    }

                    if (filter.Op is "gt" or "gte" or "lt" or "lte" &&
                        field.Type is not ("integer" or "number" or "decimal" or "money" or "date" or "datetime"))
                    {
                        throw new InvalidOperationException(
                            $"Schedule filter '{filter.Field}' cannot use an ordered comparison with field type '{field.Type}'.");
                    }

                    var scalarName = $"schedule_filter_{filterIndex}_0";
                    parameters.Add((scalarName, ConvertLiteral(field, filter.Value)));
                    var sqlOperator = filter.Op switch
                    {
                        "eq" => "=",
                        "neq" => "<>",
                        "gt" => ">",
                        "gte" => ">=",
                        "lt" => "<",
                        "lte" => "<=",
                        _ => throw new InvalidOperationException("Unsupported schedule comparison operator.")
                    };
                    clauses.Add($"{column} {sqlOperator} @{scalarName}");
                    break;
                case "in":
                case "not_in":
                    if (filter.Value is not null || filter.Values.Count is < 1 or > 16)
                    {
                        throw new InvalidOperationException($"Schedule filter '{filter.Field}' has an invalid list operand.");
                    }

                    var names = new List<string>(filter.Values.Count);
                    for (var valueIndex = 0; valueIndex < filter.Values.Count; valueIndex++)
                    {
                        var name = $"schedule_filter_{filterIndex}_{valueIndex}";
                        names.Add($"@{name}");
                        parameters.Add((name, ConvertLiteral(field, filter.Values[valueIndex])));
                    }

                    clauses.Add($"{column} {(filter.Op == "in" ? "in" : "not in")} ({string.Join(", ", names)})");
                    break;
                case "exists":
                case "not_exists":
                    if (filter.Value is not null || filter.Values.Count != 0)
                    {
                        throw new InvalidOperationException($"Schedule filter '{filter.Field}' has an invalid existence operand.");
                    }

                    clauses.Add($"{column} is {(filter.Op == "exists" ? "not " : string.Empty)}null");
                    break;
                default:
                    throw new InvalidOperationException($"Schedule filter '{filter.Field}' uses unsupported operator '{filter.Op}'.");
            }
        }

        return string.Join(" and ", clauses);
    }

    private static void ValidateBinding(
        ScheduleDefinition definition,
        EntityDefinition entity,
        WorkflowDefinition workflow,
        string persistedId,
        string persistedEntity,
        string persistedDueField,
        string persistedWorkflow,
        string persistedHash)
    {
        if (!definition.Id.Equals(persistedId, StringComparison.Ordinal) ||
            !definition.Entity.Equals(persistedEntity, StringComparison.Ordinal) ||
            !definition.DueField.Equals(persistedDueField, StringComparison.Ordinal) ||
            !definition.Workflow.Equals(persistedWorkflow, StringComparison.Ordinal) ||
            !definition.DefinitionHash.Equals(persistedHash, StringComparison.Ordinal) ||
            !entity.Id.Equals(definition.Entity, StringComparison.Ordinal) ||
            !workflow.Id.Equals(definition.Workflow, StringComparison.Ordinal) ||
            !workflow.Entity.Equals(definition.Entity, StringComparison.Ordinal) ||
            workflow.Trigger is not null)
        {
            throw new InvalidOperationException("Persisted schedule metadata does not match its authoritative compiled lineage.");
        }

        if (!ComputeDefinitionHash(definition).Equals(definition.DefinitionHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted schedule definition hash does not match its canonical metadata.");
        }

        EnsureIdentifier(definition.Id);
        EnsureIdentifier(entity.TableName);
        var dueField = entity.Fields.SingleOrDefault(field =>
            field.Id.Equals(definition.DueField, StringComparison.Ordinal));
        if (dueField is null ||
            !dueField.Type.Equals("datetime", StringComparison.Ordinal) ||
            dueField.Computed is not null)
        {
            throw new InvalidOperationException("Persisted schedule due metadata is not one exact materialized datetime field.");
        }

        EnsureMaterializedField(dueField, "schedule due field");
        foreach (var filter in definition.Filters)
        {
            var field = entity.Fields.SingleOrDefault(candidate =>
                candidate.Id.Equals(filter.Field, StringComparison.Ordinal));
            if (field is null)
            {
                throw new InvalidOperationException($"Persisted schedule filter field '{filter.Field}' is not canonical.");
            }

            EnsureMaterializedField(field, $"schedule filter '{filter.Field}'");
        }
    }

    private static object ConvertLiteral(FieldDefinition field, TypedLiteralDefinition literal)
    {
        var expectedType = field.Type switch
        {
            "string" or "text" or "enum" => "string",
            "integer" or "relation" => "integer",
            "number" or "decimal" or "money" => "decimal",
            "boolean" => "boolean",
            "date" => "date",
            "datetime" => "datetime",
            _ => throw new InvalidOperationException($"Schedule filter field '{field.Id}' has unsupported type '{field.Type}'.")
        };
        if (!literal.Type.Equals(expectedType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Schedule filter literal for '{field.Id}' does not match its exact field type.");
        }

        return literal.Type switch
        {
            "string" => literal.Value,
            "integer" => long.Parse(literal.Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            "decimal" => decimal.Parse(literal.Value, NumberStyles.Number, CultureInfo.InvariantCulture),
            "boolean" => bool.Parse(literal.Value),
            "date" => DateOnly.ParseExact(literal.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "datetime" => DateTimeOffset.Parse(literal.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _ => throw new InvalidOperationException($"Schedule filter literal type '{literal.Type}' is unsupported.")
        };
    }

    private static string ComputeDefinitionHash(ScheduleDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", definition.Id);
            writer.WriteString("entity", definition.Entity);
            writer.WriteString("due_field", definition.DueField);
            writer.WritePropertyName("filters");
            writer.WriteStartArray();
            foreach (var filter in definition.Filters)
            {
                writer.WriteStartObject();
                writer.WriteString("field", filter.Field);
                writer.WriteString("op", filter.Op);
                if (filter.Value is not null)
                {
                    writer.WritePropertyName("value");
                    WriteLiteral(writer, filter.Value);
                }

                writer.WritePropertyName("values");
                writer.WriteStartArray();
                foreach (var literal in filter.Values)
                {
                    WriteLiteral(writer, literal);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("workflow", definition.Workflow);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteLiteral(Utf8JsonWriter writer, TypedLiteralDefinition literal)
    {
        writer.WriteStartObject();
        writer.WriteString("type", literal.Type);
        writer.WriteString("value", literal.Value);
        writer.WriteEndObject();
    }

    private static void AddDynamicParameters(
        NpgsqlCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static void EnsureMaterializedField(FieldDefinition field, string context)
    {
        if (field.Computed is not null)
        {
            throw new InvalidOperationException($"Persisted {context} must be materialized.");
        }

        EnsureIdentifier(field.ColumnName);
    }

    private TimeSpan RetryDelay(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var ticks = options.BaseRetryDelay.Ticks * Math.Pow(2, exponent);
        return TimeSpan.FromTicks((long)Math.Min(ticks, options.MaxRetryDelay.Ticks));
    }

    private static void AddLeaseParameters(
        NpgsqlCommand command,
        RuntimeScheduleLease lease,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("jobId", lease.JobId);
        command.Parameters.AddWithValue("definitionHash", lease.ScheduleDefinitionHash);
        command.Parameters.AddWithValue("leaseId", lease.LeaseId);
        command.Parameters.AddWithValue("workerId", lease.WorkerId);
        command.Parameters.AddWithValue("now", now);
    }

    private static string LeasePredicate(string alias) =>
        $"{alias}.id = @jobId and {alias}.status = 'running' " +
        $"and {alias}.schedule_definition_hash = @definitionHash " +
        $"and {alias}.lease_id = @leaseId and {alias}.leased_by = @workerId";

    private static void ValidateWorkerId(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 120)
        {
            throw new ArgumentException("A bounded scheduler worker id is required.", nameof(workerId));
        }
    }

    private static void EnsureIdentifier(string value)
    {
        if (!IdentifierPattern.IsMatch(value))
        {
            throw new InvalidOperationException("Persisted schedule metadata contains an unsafe SQL identifier.");
        }
    }

    private static string Quote(string value)
    {
        EnsureIdentifier(value);
        return $"\"{value}\"";
    }

    private static RuntimeScheduledJob ReadJob(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetString(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetGuid(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        reader.IsDBNull(13) ? null : reader.GetInt64(13),
        reader.IsDBNull(14) ? null : reader.GetGuid(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.GetFieldValue<DateTimeOffset>(16),
        reader.GetFieldValue<DateTimeOffset>(17),
        reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18));

    private const string JobSelect = """
        select id, schedule_id, schedule_definition_hash, entity_id, record_id,
               due_at, status, attempt, max_attempts, available_at, lease_id,
               leased_by, lease_expires_at, workflow_instance_id, acceptance_run_id,
               last_error_code, created_at, updated_at, completed_at
        from _runtime.scheduled_jobs
        """;

    private static string ReturningColumns(string alias) =>
        $"{alias}.id, {alias}.schedule_id, {alias}.schedule_definition_hash, {alias}.entity_id, " +
        $"{alias}.record_id, {alias}.due_at, {alias}.status, {alias}.attempt, {alias}.max_attempts, " +
        $"{alias}.available_at, {alias}.lease_id, {alias}.leased_by, {alias}.lease_expires_at, " +
        $"{alias}.workflow_instance_id, {alias}.acceptance_run_id, {alias}.last_error_code, " +
        $"{alias}.created_at, {alias}.updated_at, {alias}.completed_at";

    private sealed record RuntimeScheduleBinding(
        ScheduleDefinition Definition,
        EntityDefinition Entity)
    {
        public FieldDefinition DueField => Entity.Fields.Single(candidate =>
            candidate.Id.Equals(Definition.DueField, StringComparison.Ordinal));
    }
}
