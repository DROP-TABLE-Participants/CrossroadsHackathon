using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeActionStore
{
    private static readonly IReadOnlySet<string> ActionReadableSystemFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id",
            "created_at",
            "created_by",
            "updated_at",
            "updated_by",
            "archived_at",
            "archived_by",
            "archive_reason"
        };

    private void EnsureActionFieldAllowed(
        RuntimePermissionSnapshot permissionSnapshot,
        string permissionAction,
        RuntimeEntityDefinition entity,
        string fieldKey)
    {
        // Resolve declared fields first. Persisted field metadata is untrusted,
        // and a declared field that collides with a system-field name must not
        // inherit the system field's read exemption.
        var field = ResolveField(entity, fieldKey);
        if (field is null &&
            permissionAction.Equals("read", StringComparison.OrdinalIgnoreCase) &&
            ActionReadableSystemFields.Contains(fieldKey))
        {
            return;
        }

        if (field is null)
        {
            throw new RuntimeActionValidationException(
                $"Action references unknown field '{fieldKey}' on {entity.Key}.");
        }

        if (!permissions.IsFieldAllowed(permissionSnapshot, permissionAction, entity, field.Name))
        {
            throw new RuntimeActionPermissionException(
                $"field.{permissionAction.Trim().ToLowerInvariant()}",
                $"{entity.Key}.{field.Name}",
                "Action field permission denied.");
        }
    }

    private void EnsureActionWritableValuesAllowed(
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeEntityDefinition entity,
        IEnumerable<string> fieldKeys,
        string mutationAction)
    {
        var fieldAction = mutationAction.StartsWith("effect_", StringComparison.OrdinalIgnoreCase)
            ? "effect_write"
            : "write";
        foreach (var fieldKey in fieldKeys)
        {
            EnsureActionFieldAllowed(permissionSnapshot, fieldAction, entity, fieldKey);
        }
    }

    private void EnsureValueSourceFieldsAllowed(
        RuntimePermissionSnapshot permissionSnapshot,
        ActionValueDefinition definition,
        RuntimeActionContext context,
        RuntimeEntityDefinition? itemEntity)
    {
        if (definition.ValueFrom is not null)
        {
            EnsureValueFromSourceFieldsAllowed(
                permissionSnapshot,
                definition.ValueFrom,
                context,
                itemEntity);
            return;
        }

        if (!string.IsNullOrWhiteSpace(definition.Template))
        {
            EnsureTemplateSourceFieldsAllowed(
                permissionSnapshot,
                definition.Template!,
                context,
                itemEntity);
            return;
        }

        if (definition.Aggregate is not null)
        {
            var query = ResolveQueryResult(context, definition.Aggregate.Result, definition.Aggregate.Result);
            EnsureExpressionSourceFieldsAllowed(
                permissionSnapshot,
                definition.Aggregate.Expression,
                context,
                query.Entity);
        }
    }

    private void EnsureValueFromSourceFieldsAllowed(
        RuntimePermissionSnapshot permissionSnapshot,
        ActionValueFromDefinition valueFrom,
        RuntimeActionContext context,
        RuntimeEntityDefinition? itemEntity)
    {
        if (!string.IsNullOrWhiteSpace(valueFrom.Record))
        {
            EnsureActionFieldAllowed(
                permissionSnapshot,
                "read",
                context.SourceEntity,
                valueFrom.Record!);
            return;
        }

        if (!string.IsNullOrWhiteSpace(valueFrom.Item))
        {
            if (itemEntity is not null)
            {
                EnsureActionFieldAllowed(permissionSnapshot, "read", itemEntity, valueFrom.Item!);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(valueFrom.Step) &&
            context.CreatedRecords.TryGetValue(valueFrom.Step!, out var created))
        {
            EnsureActionFieldAllowed(
                permissionSnapshot,
                "read",
                created.Entity,
                string.IsNullOrWhiteSpace(valueFrom.Field) ? "id" : valueFrom.Field!);
        }
    }

    private void EnsureConditionSourceFieldsAllowed(
        RuntimePermissionSnapshot permissionSnapshot,
        ActionConditionDefinition condition,
        RuntimeActionContext context,
        RuntimeEntityDefinition? itemEntity)
    {
        foreach (var child in condition.Conditions)
        {
            EnsureConditionSourceFieldsAllowed(permissionSnapshot, child, context, itemEntity);
        }

        EnsureOperandSourceFieldsAllowed(permissionSnapshot, condition.Left, context, itemEntity);
        EnsureOperandSourceFieldsAllowed(permissionSnapshot, condition.Right, context, itemEntity);
    }

    private void EnsureExpressionSourceFieldsAllowed(
        RuntimePermissionSnapshot permissionSnapshot,
        ActionExpressionDefinition? expression,
        RuntimeActionContext context,
        RuntimeEntityDefinition itemEntity)
    {
        if (expression is null)
        {
            return;
        }

        EnsureOperandSourceFieldsAllowed(permissionSnapshot, expression.Left, context, itemEntity);
        EnsureOperandSourceFieldsAllowed(permissionSnapshot, expression.Right, context, itemEntity);
    }

    private void EnsureOperandSourceFieldsAllowed(
        RuntimePermissionSnapshot permissionSnapshot,
        ActionOperandDefinition? operand,
        RuntimeActionContext context,
        RuntimeEntityDefinition? itemEntity)
    {
        if (operand is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(operand.Field))
        {
            EnsureActionFieldAllowed(
                permissionSnapshot,
                "read",
                context.SourceEntity,
                operand.Field!);
            return;
        }

        if (operand.ValueFrom is not null)
        {
            EnsureValueFromSourceFieldsAllowed(
                permissionSnapshot,
                operand.ValueFrom,
                context,
                itemEntity);
        }
    }

    private void EnsureTemplateSourceFieldsAllowed(
        RuntimePermissionSnapshot permissionSnapshot,
        string template,
        RuntimeActionContext context,
        RuntimeEntityDefinition? itemEntity)
    {
        foreach (Match match in TemplateTokenPattern.Matches(template))
        {
            var token = match.Groups[1].Value;
            if (token.StartsWith("record.", StringComparison.OrdinalIgnoreCase))
            {
                EnsureActionFieldAllowed(
                    permissionSnapshot,
                    "read",
                    context.SourceEntity,
                    token["record.".Length..]);
                continue;
            }

            if (token.StartsWith("item.", StringComparison.OrdinalIgnoreCase) && itemEntity is not null)
            {
                EnsureActionFieldAllowed(
                    permissionSnapshot,
                    "read",
                    itemEntity,
                    token["item.".Length..]);
                continue;
            }

            if (!token.StartsWith("step.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segments = token.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length >= 3 && context.CreatedRecords.TryGetValue(segments[1], out var created))
            {
                EnsureActionFieldAllowed(permissionSnapshot, "read", created.Entity, segments[2]);
            }
        }
    }

    private static bool IsFieldPermissionDenial(RuntimeActionPermissionException exception) =>
        exception.PermissionAction.StartsWith("field.", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> AreExecutableStepFieldsAllowedAsync(
        NpgsqlConnection connection,
        RuntimePermissionSnapshot permissionSnapshot,
        ActionDefinition action,
        RuntimeEntityDefinition sourceEntity,
        IReadOnlyList<RuntimeActionAuthorizationRequirement> requirements,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = new RuntimeActionContext(
                sourceEntity,
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

            foreach (var step in action.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Type))
                {
                    throw new RuntimeActionValidationException(
                        $"Action step '{step.Id}' has no type.");
                }

                switch (step.Type.Trim().ToLowerInvariant())
                {
                    case "query_records":
                    {
                        var queryEntity = await LoadActionDependencyEntityAsync(
                            connection,
                            step.Entity,
                            step.Id,
                            cancellationToken);
                        foreach (var filter in step.Where)
                        {
                            var field = ResolveActionFilterField(
                                queryEntity,
                                filter,
                                context);
                            EnsureActionFieldAllowed(
                                permissionSnapshot,
                                "read",
                                queryEntity,
                                field.Name);
                            if (filter.ValueFrom is not null)
                            {
                                EnsureValueFromSourceFieldsAllowed(
                                    permissionSnapshot,
                                    filter.ValueFrom,
                                    context,
                                    itemEntity: null);
                            }
                        }

                        context.QueryResults[step.Id] = new RuntimeActionQueryResult(
                            queryEntity,
                            [],
                            HasHiddenMatches: false);
                        break;
                    }
                    case "guard":
                        if (step.Condition is null)
                        {
                            throw new RuntimeActionValidationException(
                                $"Action guard step '{step.Id}' has no condition.");
                        }

                        EnsureConditionSourceFieldsAllowed(
                            permissionSnapshot,
                            step.Condition,
                            context,
                            itemEntity: null);
                        break;
                    case "create_record":
                    {
                        var createdEntity = await LoadActionDependencyEntityAsync(
                            connection,
                            step.Entity,
                            step.Id,
                            cancellationToken);
                        EnsureActionValueDependenciesAllowed(
                            permissionSnapshot,
                            createdEntity,
                            step.Values,
                            context,
                            itemEntity: null,
                            ResolveMutationPermissionAction(requirements, createdEntity, "create"));
                        context.CreatedRecords[step.Id] = new RuntimeActionCreated(
                            createdEntity,
                            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
                        break;
                    }
                    case "create_records_for_each":
                    {
                        var createdEntity = await LoadActionDependencyEntityAsync(
                            connection,
                            step.Entity,
                            step.Id,
                            cancellationToken);
                        var query = ResolveQueryResult(context, step.ForEach?.Result, step.Id);
                        EnsureActionValueDependenciesAllowed(
                            permissionSnapshot,
                            createdEntity,
                            step.Values,
                            context,
                            query.Entity,
                            ResolveMutationPermissionAction(requirements, createdEntity, "create"));
                        break;
                    }
                    case "update_records":
                    {
                        var query = ResolveQueryResult(context, step.RecordsFrom?.Result, step.Id);
                        EnsureActionValueDependenciesAllowed(
                            permissionSnapshot,
                            query.Entity,
                            step.Set,
                            context,
                            query.Entity,
                            ResolveMutationPermissionAction(requirements, query.Entity, "update"));
                        break;
                    }
                    case "update_current_record":
                        EnsureActionValueDependenciesAllowed(
                            permissionSnapshot,
                            sourceEntity,
                            step.Set,
                            context,
                            itemEntity: null,
                            ResolveMutationPermissionAction(requirements, sourceEntity, "update"));
                        break;
                    case "audit_event":
                        foreach (var definition in step.Metadata.Values)
                        {
                            EnsureValueSourceFieldsAllowed(
                                permissionSnapshot,
                                definition,
                                context,
                                itemEntity: null);
                        }

                        break;
                    case "return_result":
                        break;
                    default:
                        throw new RuntimeActionValidationException(
                            $"Unsupported action step '{step.Type}'.");
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is RuntimeActionPermissionException or RuntimeActionValidationException)
        {
            // Listing is a discovery boundary. An action whose persisted
            // definition cannot be proven executable without denied fields is
            // unavailable rather than partially disclosed or allowed to fail
            // only after the user selects it.
            return false;
        }
    }

    private static async Task<RuntimeEntityDefinition> LoadActionDependencyEntityAsync(
        NpgsqlConnection connection,
        string? entityId,
        string stepId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entityId) &&
            await LoadEntityByIdAsync(connection, entityId, cancellationToken) is { } entity)
        {
            return entity;
        }

        throw new RuntimeActionValidationException(
            $"Action step '{stepId}' references unknown entity '{entityId}'.");
    }

    private void EnsureActionValueDependenciesAllowed(
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeEntityDefinition targetEntity,
        IReadOnlyDictionary<string, ActionValueDefinition> definitions,
        RuntimeActionContext context,
        RuntimeEntityDefinition? itemEntity,
        string mutationAction)
    {
        EnsureActionWritableValuesAllowed(
            permissionSnapshot,
            targetEntity,
            definitions.Keys,
            mutationAction);
        foreach (var definition in definitions.Values)
        {
            EnsureValueSourceFieldsAllowed(permissionSnapshot, definition, context, itemEntity);
        }
    }
}
