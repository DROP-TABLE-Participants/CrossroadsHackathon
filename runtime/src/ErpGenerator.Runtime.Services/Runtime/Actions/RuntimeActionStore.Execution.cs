using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeActionStore
{
    private async Task ExecuteStepAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimePermissionSnapshot permissionSnapshot,
        ActionDefinition action,
        ActionStepDefinition step,
        RuntimeActionContext context,
        IReadOnlyList<RuntimeActionAuthorizationRequirement> requirements,
        CancellationToken cancellationToken)
    {
        switch (step.Type)
        {
            case "query_records":
            {
                var entity = await LoadEntityByIdAsync(connection, step.Entity!, cancellationToken)
                             ?? throw new RuntimeActionValidationException($"Action step '{step.Id}' references a missing entity.");
                var query = await QueryRecordsAsync(
                    connection,
                    transaction,
                    permissionSnapshot,
                    user,
                    entity,
                    step.Where,
                    ResolveQueryLockMode(action, step.Id),
                    context,
                    cancellationToken);
                if (IsQueryCountConsumedByGuard(action, step.Id))
                {
                    EnsureQueryReadIsComplete(query);
                }

                context.QueryResults[step.Id] = query;
                context.Counts[step.Id] = query.Records.Count;
                break;
            }
            case "guard":
                if (step.Condition is not null)
                {
                    EnsureConditionSourceFieldsAllowed(
                        permissionSnapshot,
                        step.Condition,
                        context,
                        itemEntity: null);
                }

                if (step.Condition is null || !EvaluateCondition(step.Condition, context, null))
                {
                    throw new RuntimeActionGuardRejectedException(
                        action.Key,
                        step.Id,
                        step.Error ?? "Action guard failed.");
                }

                break;
            case "create_record":
            {
                var entity = await LoadEntityByIdAsync(connection, step.Entity!, cancellationToken)
                             ?? throw new RuntimeActionValidationException($"Action step '{step.Id}' references a missing entity.");
                var values = await ResolveValuesAsync(
                    connection,
                    transaction,
                    permissionSnapshot,
                    user,
                    entity,
                    step.Values,
                    context,
                    null,
                    itemEntity: null,
                    true,
                    ResolveMutationPermissionAction(requirements, entity, "create"),
                    cancellationToken);
                var mutationAction = ResolveMutationPermissionAction(requirements, entity, "create");
                var record = await InsertRecordAsync(
                    connection,
                    transaction,
                    tenant,
                    user,
                    permissionSnapshot,
                    entity,
                    values,
                    mutationAction,
                    cancellationToken);
                context.CreatedRecords[step.Id] = new RuntimeActionCreated(entity, record);
                context.Counts[step.Id] = 1;
                break;
            }
            case "create_records_for_each":
            {
                var entity = await LoadEntityByIdAsync(connection, step.Entity!, cancellationToken)
                             ?? throw new RuntimeActionValidationException($"Action step '{step.Id}' references a missing entity.");
                var query = ResolveQueryResult(context, step.ForEach?.Result, step.Id);
                EnsureQueryReadIsComplete(query);
                if (query.Records.Count > MaxActionRecords)
                {
                    throw new RuntimeActionValidationException($"Action step '{step.Id}' matched more than {MaxActionRecords} records.");
                }

                var created = new List<Dictionary<string, object?>>();
                foreach (var item in query.Records)
                {
                    var values = await ResolveValuesAsync(
                        connection,
                        transaction,
                        permissionSnapshot,
                        user,
                        entity,
                        step.Values,
                        context,
                        item,
                        query.Entity,
                        true,
                        ResolveMutationPermissionAction(requirements, entity, "create"),
                        cancellationToken);
                    var mutationAction = ResolveMutationPermissionAction(requirements, entity, "create");
                    created.Add(await InsertRecordAsync(
                        connection,
                        transaction,
                        tenant,
                        user,
                        permissionSnapshot,
                        entity,
                        values,
                        mutationAction,
                        cancellationToken));
                }

                context.CreatedRecordSets[step.Id] = new RuntimeActionCreatedSet(entity, created);
                context.Counts[step.Id] = created.Count;
                break;
            }
            case "update_records":
            {
                var query = ResolveQueryResult(context, step.RecordsFrom?.Result, step.Id);
                EnsureQueryReadIsComplete(query);
                if (query.Records.Count > MaxActionRecords)
                {
                    throw new RuntimeActionValidationException($"Action step '{step.Id}' matched more than {MaxActionRecords} records.");
                }

                var updated = 0;
                foreach (var item in query.Records)
                {
                    var values = await ResolveValuesAsync(
                        connection,
                        transaction,
                        permissionSnapshot,
                        user,
                        query.Entity,
                        step.Set,
                        context,
                        item,
                        query.Entity,
                        false,
                        ResolveMutationPermissionAction(requirements, query.Entity, "update"),
                        cancellationToken);
                    var mutationAction = ResolveMutationPermissionAction(requirements, query.Entity, "update");
                    var id = ToLong(item.GetValueOrDefault("id")) ?? throw new RuntimeActionValidationException("Action update_records item did not contain id.");
                    var record = await UpdateRecordAsync(
                        connection,
                        transaction,
                        tenant,
                        user,
                        permissionSnapshot,
                        query.Entity,
                        id,
                        values,
                        mutationAction,
                        cancellationToken);
                    if (record is not null)
                    {
                        updated++;
                    }
                }

                context.Counts[step.Id] = updated;
                break;
            }
            case "update_current_record":
            {
                var values = await ResolveValuesAsync(
                    connection,
                    transaction,
                    permissionSnapshot,
                    user,
                    context.SourceEntity,
                    step.Set,
                    context,
                    null,
                    itemEntity: null,
                    false,
                    ResolveMutationPermissionAction(requirements, context.SourceEntity, "update"),
                    cancellationToken);
                var mutationAction = ResolveMutationPermissionAction(
                    requirements,
                    context.SourceEntity,
                    "update");
                var id = ToLong(context.SourceRecord.GetValueOrDefault("id"))
                         ?? throw new RuntimeActionValidationException("Action source record did not contain id.");
                var record = await UpdateRecordAsync(
                                 connection,
                                 transaction,
                                 tenant,
                                 user,
                                 permissionSnapshot,
                                 context.SourceEntity,
                                 id,
                                 values,
                                 mutationAction,
                                 cancellationToken)
                             ?? throw new RuntimeActionRecordNotFoundException(context.SourceEntity.Key, id);
                context.ReplaceSourceRecord(record);
                context.Counts[step.Id] = 1;
                break;
            }
            case "audit_event":
            {
                var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["action"] = action.Key,
                    ["entity"] = context.SourceEntity.Key,
                    ["recordId"] = context.SourceRecord["id"]
                };
                foreach (var (key, value) in step.Metadata)
                {
                    EnsureValueSourceFieldsAllowed(
                        permissionSnapshot,
                        value,
                        context,
                        itemEntity: null);
                    metadata[key] = ResolveValue(value, context, null);
                }

                await WriteAuditEventAsync(
                    connection,
                    transaction,
                    tenant,
                    user,
                    step.Event ?? "action.event",
                    step.Message ?? $"Action event {step.Event}.",
                    metadata,
                    cancellationToken);
                break;
            }
            case "return_result":
                context.Return = step.Return;
                break;
            default:
                throw new RuntimeActionValidationException($"Unsupported action step '{step.Type}'.");
        }
    }

    private static void EnsureQueryReadIsComplete(RuntimeActionQueryResult query)
    {
        if (query.HasHiddenMatches)
        {
            throw RecordScopePermissionDenied("read", query.Entity);
        }
    }

    private static RuntimeActionQueryLockMode ResolveQueryLockMode(ActionDefinition action, string queryStepId)
    {
        if (action.Steps.Any(step =>
                step.Type.Equals("update_records", StringComparison.OrdinalIgnoreCase) &&
                step.RecordsFrom?.Result?.Equals(queryStepId, StringComparison.OrdinalIgnoreCase) == true))
        {
            return RuntimeActionQueryLockMode.Update;
        }

        if (IsQueryCountConsumedByGuard(action, queryStepId))
        {
            // A guard's truth must stay stable until every later effect in this
            // action transaction commits. A shared row lock prevents the exact
            // matched related record from being changed or archived between the
            // count check and the source-record mutation.
            return RuntimeActionQueryLockMode.Share;
        }

        return action.Steps.Any(step =>
            step.Type.Equals("create_records_for_each", StringComparison.OrdinalIgnoreCase) &&
            step.ForEach?.Result?.Equals(queryStepId, StringComparison.OrdinalIgnoreCase) == true)
            ? RuntimeActionQueryLockMode.Share
            : RuntimeActionQueryLockMode.None;
    }

    private static bool IsQueryCountConsumedByGuard(
        ActionDefinition action,
        string queryStepId) =>
        action.Steps.Any(step =>
            step.Type.Equals("guard", StringComparison.OrdinalIgnoreCase) &&
            ConditionConsumesQueryCount(step.Condition, queryStepId));

    private static bool ConditionConsumesQueryCount(
        ActionConditionDefinition? condition,
        string queryStepId)
    {
        if (condition is null)
        {
            return false;
        }

        return OperandConsumesQueryCount(condition.Left, queryStepId) ||
               OperandConsumesQueryCount(condition.Right, queryStepId) ||
               condition.Conditions.Any(child => ConditionConsumesQueryCount(child, queryStepId));
    }

    private static bool OperandConsumesQueryCount(
        ActionOperandDefinition? operand,
        string queryStepId) =>
        operand?.ResultCount?.Equals(queryStepId, StringComparison.OrdinalIgnoreCase) == true;

    private RuntimeActionExecutionResult BuildResult(ActionDefinition action, RuntimeActionContext context)
    {
        RuntimeActionCreatedRecord? created = null;
        var returnStep = context.Return?.Step;
        if (!string.IsNullOrWhiteSpace(returnStep) &&
            context.CreatedRecords.TryGetValue(returnStep, out var explicitCreated) &&
            string.Equals(context.Return?.Entity, explicitCreated.Entity.Id, StringComparison.Ordinal))
        {
            created = ToCreatedRecord(explicitCreated);
        }

        var message = context.Return?.Message ?? (created is null ? $"{action.Label} completed." : $"{action.Label} completed.");
        return new RuntimeActionExecutionResult(true, action.Key, message, created, context.Counts);
    }

    private static RuntimeActionCreatedRecord ToCreatedRecord(RuntimeActionCreated created)
    {
        var id = ToLong(created.Record.GetValueOrDefault("id")) ?? 0;
        return new RuntimeActionCreatedRecord(created.Entity.Key, created.Entity.Id, id, $"/entities/{created.Entity.Key}/{id}");
    }
}
