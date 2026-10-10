using ErpGenerator.Runtime.Shared.Bundles;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleActionValueValidator
{
    public static void ValidateActionValuesForEntity(
        string actionKey,
        string stepId,
        IReadOnlyDictionary<string, ErpActionValue> values,
        string path,
        string targetEntity,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        string? itemEntity,
        List<string> errors,
        bool requireCreateFields)
    {
        var fields = fieldsByEntity[targetEntity];
        var suppliedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldName, value) in values)
        {
            RequireIdentifier(fieldName, $"{path}.field", errors);
            RejectSqlLikeContent(fieldName, $"{path}.field", errors);
            if (!fields.TryGetValue(fieldName, out var field))
            {
                errors.Add($"Action '{actionKey}' step '{stepId}' references unknown field '{fieldName}' on entity '{targetEntity}'.");
                continue;
            }

            if (field.Computed is not null)
            {
                errors.Add($"Action '{actionKey}' step '{stepId}' cannot set computed field '{fieldName}'.");
                continue;
            }

            if (requireCreateFields && field.Lifecycle is not null)
            {
                errors.Add(
                    $"Action '{actionKey}' step '{stepId}' cannot set server-initialized lifecycle field '{fieldName}' while creating '{targetEntity}'.");
                continue;
            }

            suppliedFields.Add(field.Id);
            ValidateActionValue(actionKey, stepId, value, $"{path}.{fieldName}", sourceEntity, fieldsByEntity, priorSteps, itemEntity, field, errors);
        }

        if (requireCreateFields)
        {
            foreach (var field in UniqueFields(fields).Where(field =>
                         field.Required && field.Computed is null && field.Lifecycle is null))
            {
                if (!suppliedFields.Contains(field.Id))
                {
                    errors.Add($"Action '{actionKey}' step '{stepId}' create_record for '{targetEntity}' must provide required field '{field.Name}'.");
                }
            }
        }
    }

    public static void ValidateActionValueDictionary(
        string actionKey,
        string stepId,
        IReadOnlyDictionary<string, ErpActionValue> values,
        string path,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        string? itemEntity,
        List<string> errors)
    {
        foreach (var (key, value) in values)
        {
            RequireIdentifier(key, $"{path}.key", errors);
            RejectSqlLikeContent(key, $"{path}.key", errors);
            ValidateActionValue(actionKey, stepId, value, $"{path}.{key}", sourceEntity, fieldsByEntity, priorSteps, itemEntity, targetField: null, errors);
        }
    }

    public static void ValidateActionCondition(
        string actionKey,
        ErpActionCondition condition,
        string path,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        string? itemEntity,
        List<string> errors)
    {
        var op = condition.Op.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(op))
        {
            errors.Add($"Action '{actionKey}' condition '{path}' must declare op.");
            return;
        }

        if (!WorkflowConditionOps.Contains(op))
        {
            errors.Add($"Action '{actionKey}' condition '{path}' uses unsupported op '{condition.Op}'.");
            return;
        }

        RejectSqlLikeContent(condition.Op, $"{path}.op", errors);
        if (op is "and" or "or")
        {
            if (condition.Conditions.Count == 0)
            {
                errors.Add($"Action '{actionKey}' condition '{path}' op '{op}' must declare conditions.");
                return;
            }

            for (var index = 0; index < condition.Conditions.Count; index++)
            {
                ValidateActionCondition(actionKey, condition.Conditions[index], $"{path}.conditions[{index}]", sourceEntity, fieldsByEntity, priorSteps, itemEntity, errors);
            }

            return;
        }

        ValidateActionOperand(actionKey, "condition", condition.Left, $"{path}.left", sourceEntity, fieldsByEntity, priorSteps, itemEntity, errors, required: true);
        if (op is not "exists" and not "not_exists")
        {
            ValidateActionOperand(actionKey, "condition", condition.Right, $"{path}.right", sourceEntity, fieldsByEntity, priorSteps, itemEntity, errors, required: true);
        }
    }

    public static void ValidateActionValueFrom(
        string actionKey,
        string stepId,
        ErpActionValueFrom valueFrom,
        string path,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        string? itemEntity,
        ErpField? targetField,
        List<string> errors,
        string? comparisonOp = null)
    {
        RejectSqlLikeContent(valueFrom.Record, $"{path}.record", errors);
        RejectSqlLikeContent(valueFrom.Item, $"{path}.item", errors);
        RejectSqlLikeContent(valueFrom.Step, $"{path}.step", errors);
        RejectSqlLikeContent(valueFrom.Field, $"{path}.field", errors);
        RejectSqlLikeContent(valueFrom.Now, $"{path}.now", errors);

        var specified = new[]
        {
            !string.IsNullOrWhiteSpace(valueFrom.Record),
            !string.IsNullOrWhiteSpace(valueFrom.Item),
            !string.IsNullOrWhiteSpace(valueFrom.Step),
            !string.IsNullOrWhiteSpace(valueFrom.Now)
        }.Count(value => value);

        if (specified != 1)
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' value_from '{path}' must declare exactly one of record, item, step, or now.");
            return;
        }

        if (IsOrderedComparisonOperator(comparisonOp) &&
            !string.IsNullOrWhiteSpace(valueFrom.Record))
        {
            ValidateOrderedRecordComparison(
                actionKey,
                stepId,
                sourceEntity,
                valueFrom.Record,
                targetField,
                fieldsByEntity,
                errors);
        }

        if (!string.IsNullOrWhiteSpace(valueFrom.Record))
        {
            ValidateActionSourceField(actionKey, stepId, path, "record", sourceEntity, valueFrom.Record, fieldsByEntity, targetField, errors);
        }

        if (!string.IsNullOrWhiteSpace(valueFrom.Item))
        {
            if (itemEntity is null)
            {
                errors.Add($"Action '{actionKey}' step '{stepId}' value_from.item is only valid inside foreach or aggregate contexts.");
            }
            else
            {
                ValidateActionSourceField(actionKey, stepId, path, "item", itemEntity, valueFrom.Item, fieldsByEntity, targetField, errors);
            }
        }

        if (!string.IsNullOrWhiteSpace(valueFrom.Step))
        {
            RequireIdentifier(valueFrom.Step, $"{path}.step", errors);
            if (!priorSteps.TryGetValue(valueFrom.Step, out var stepContext))
            {
                errors.Add($"Action '{actionKey}' step '{stepId}' references unknown prior step '{valueFrom.Step}'.");
            }
            else
            {
                var field = string.IsNullOrWhiteSpace(valueFrom.Field) ? "id" : valueFrom.Field;
                ValidateActionSourceField(actionKey, stepId, path, "step", stepContext.Entity, field, fieldsByEntity, targetField, errors);
            }
        }

        if (!string.IsNullOrWhiteSpace(valueFrom.Now))
        {
            var now = valueFrom.Now.Trim().ToLowerInvariant();
            if (now is not "date" and not "datetime" and not "timestamp")
            {
                errors.Add($"Action '{actionKey}' step '{stepId}' value_from.now uses unsupported value '{valueFrom.Now}'.");
            }
        }
    }

    public static bool HasAnyValueFrom(ErpActionValueFrom? valueFrom) =>
        valueFrom is not null &&
        (!string.IsNullOrWhiteSpace(valueFrom.Record) ||
         !string.IsNullOrWhiteSpace(valueFrom.Item) ||
         !string.IsNullOrWhiteSpace(valueFrom.Step) ||
         !string.IsNullOrWhiteSpace(valueFrom.Now));

    static void ValidateOrderedRecordComparison(
        string actionKey,
        string stepId,
        string sourceEntity,
        string sourceFieldName,
        ErpField? targetField,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        if (targetField is null)
        {
            return;
        }

        var targetType = NormalizeOrderedComparisonFieldType(targetField.Type);
        if (targetField.Computed is not null)
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' ordered comparison target field '{targetField.Id}' must be materialized and noncomputed.");
        }

        if (targetType is null)
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' ordered comparison target field '{targetField.Id}' must use integer, number, decimal, money, date, or datetime, not '{targetField.Type}'.");
        }

        if (sourceFieldName.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' ordered comparison value_from.record cannot use system identity 'id'.");
            return;
        }

        if (!fieldsByEntity.TryGetValue(sourceEntity, out var sourceFields) ||
            !sourceFields.TryGetValue(sourceFieldName, out var sourceField))
        {
            return;
        }

        var sourceType = NormalizeOrderedComparisonFieldType(sourceField.Type);
        if (sourceField.Computed is not null)
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' ordered comparison value_from.record field '{sourceEntity}.{sourceField.Id}' must be materialized and noncomputed.");
        }

        if (sourceType is null)
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' ordered comparison value_from.record field '{sourceEntity}.{sourceField.Id}' must use integer, number, decimal, money, date, or datetime, not '{sourceField.Type}'.");
        }

        if (targetType is not null &&
            sourceType is not null &&
            !targetType.Equals(sourceType, StringComparison.Ordinal))
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' ordered comparison requires exact compatible scalar types after normalization; target field '{targetField.Id}' is '{targetField.Type}' and value_from.record field '{sourceEntity}.{sourceField.Id}' is '{sourceField.Type}'.");
        }
    }

    static bool IsOrderedComparisonOperator(string? op) =>
        op?.Trim().ToLowerInvariant() is "gt" or "gte" or "lt" or "lte";

    static string? NormalizeOrderedComparisonFieldType(string? type) =>
        type?.Trim().ToLowerInvariant() switch
        {
            "integer" => "integer",
            "number" or "decimal" => "number",
            "money" => "money",
            "date" => "date",
            "datetime" => "datetime",
            _ => null
        };

    static void ValidateActionValue(
        string actionKey,
        string stepId,
        ErpActionValue value,
        string path,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        string? itemEntity,
        ErpField? targetField,
        List<string> errors)
    {
        RejectSqlLikeValue(value.Value, $"{path}.value", errors);
        RejectSqlLikeContent(value.Template, $"{path}.template", errors);

        var specified = new[]
        {
            value.Value is not null,
            value.ValueFrom is not null && HasAnyValueFrom(value.ValueFrom),
            !string.IsNullOrWhiteSpace(value.Template),
            value.Aggregate is not null
        }.Count(item => item);

        if (specified != 1)
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' value '{path}' must declare exactly one of value, value_from, template, or aggregate.");
            return;
        }

        if (targetField is not null && value.Value is not null && !IsValueCompatible(targetField, value.Value))
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' assigns an incompatible value to field '{targetField.Name}'.");
        }

        if (value.ValueFrom is not null)
        {
            ValidateActionValueFrom(actionKey, stepId, value.ValueFrom, $"{path}.value_from", sourceEntity, fieldsByEntity, priorSteps, itemEntity, targetField, errors);
        }

        if (value.Aggregate is not null)
        {
            ValidateActionAggregate(actionKey, stepId, value.Aggregate, $"{path}.aggregate", sourceEntity, fieldsByEntity, priorSteps, errors);
        }
    }

    static void ValidateActionAggregate(
        string actionKey,
        string stepId,
        ErpActionAggregate aggregate,
        string path,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        List<string> errors)
    {
        RejectSqlLikeContent(aggregate.Result, $"{path}.result", errors);
        RejectSqlLikeContent(aggregate.Op, $"{path}.op", errors);

        if (!ActionAggregateOps.Contains(aggregate.Op))
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' aggregate uses unsupported op '{aggregate.Op}'.");
        }

        if (string.IsNullOrWhiteSpace(aggregate.Result) ||
            !priorSteps.TryGetValue(aggregate.Result, out var result) ||
            !result.Type.Equals("query_records", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' aggregate references unknown query result '{aggregate.Result}'.");
            return;
        }

        if (aggregate.Expression is null)
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' aggregate must declare expression.");
            return;
        }

        ValidateActionExpression(actionKey, stepId, aggregate.Expression, $"{path}.expression", sourceEntity, fieldsByEntity, priorSteps, result.Entity, errors);
    }

    static void ValidateActionExpression(
        string actionKey,
        string stepId,
        ErpActionExpression expression,
        string path,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        string itemEntity,
        List<string> errors)
    {
        RejectSqlLikeContent(expression.Op, $"{path}.op", errors);
        if (!ActionExpressionOps.Contains(expression.Op))
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' expression uses unsupported op '{expression.Op}'.");
            return;
        }

        ValidateActionOperand(actionKey, stepId, expression.Left, $"{path}.left", sourceEntity, fieldsByEntity, priorSteps, itemEntity, errors, required: true);
        ValidateActionOperand(actionKey, stepId, expression.Right, $"{path}.right", sourceEntity, fieldsByEntity, priorSteps, itemEntity, errors, required: true);
    }

    static void ValidateActionOperand(
        string actionKey,
        string stepId,
        ErpActionOperand? operand,
        string path,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        string? itemEntity,
        List<string> errors,
        bool required)
    {
        if (operand is null)
        {
            if (required)
            {
                errors.Add($"Action '{actionKey}' step '{stepId}' operand '{path}' is required.");
            }

            return;
        }

        RejectSqlLikeContent(operand.Field, $"{path}.field", errors);
        RejectSqlLikeContent(operand.ResultCount, $"{path}.result_count", errors);
        RejectSqlLikeValue(operand.Value, $"{path}.value", errors);

        var specified = new[]
        {
            !string.IsNullOrWhiteSpace(operand.Field),
            operand.Value is not null,
            !string.IsNullOrWhiteSpace(operand.ResultCount),
            operand.ValueFrom is not null && HasAnyValueFrom(operand.ValueFrom)
        }.Count(value => value);

        if (specified != 1)
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' operand '{path}' must declare exactly one of field, value, result_count, or value_from.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(operand.Field))
        {
            RequireIdentifier(operand.Field, $"{path}.field", errors);
            if (!fieldsByEntity[sourceEntity].ContainsKey(operand.Field))
            {
                errors.Add($"Action '{actionKey}' step '{stepId}' operand references unknown field '{operand.Field}' on entity '{sourceEntity}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(operand.ResultCount))
        {
            RequireIdentifier(operand.ResultCount, $"{path}.result_count", errors);
            if (!priorSteps.TryGetValue(operand.ResultCount, out var result) || !result.Type.Equals("query_records", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Action '{actionKey}' step '{stepId}' operand references unknown query result '{operand.ResultCount}'.");
            }
        }

        if (operand.ValueFrom is not null)
        {
            ValidateActionValueFrom(actionKey, stepId, operand.ValueFrom, $"{path}.value_from", sourceEntity, fieldsByEntity, priorSteps, itemEntity, targetField: null, errors);
        }
    }

    static void ValidateActionSourceField(
        string actionKey,
        string stepId,
        string path,
        string sourceKind,
        string sourceEntity,
        string fieldName,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        ErpField? targetField,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' {sourceKind} source in '{path}' must declare a field.");
            return;
        }

        if (fieldName.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            ValidateRelationTarget(actionKey, stepId, sourceEntity, sourceField: null, targetField, errors);
            return;
        }

        RequireIdentifier(fieldName, $"{path}.{sourceKind}", errors);
        if (!fieldsByEntity.TryGetValue(sourceEntity, out var fields) || !fields.TryGetValue(fieldName, out var sourceField))
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' references unknown {sourceKind} field '{fieldName}' on entity '{sourceEntity}'.");
            return;
        }

        ValidateRelationTarget(actionKey, stepId, sourceEntity, sourceField, targetField, errors);
    }

    static void ValidateRelationTarget(
        string actionKey,
        string stepId,
        string sourceEntity,
        ErpField? sourceField,
        ErpField? targetField,
        List<string> errors)
    {
        if (targetField is null || !targetField.Type.Equals("relation", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceReferences = sourceField is null
            ? sourceEntity
            : sourceField.Type.Equals("relation", StringComparison.OrdinalIgnoreCase)
                ? sourceField.References
                : null;

        if (sourceReferences is null)
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' assigns non-relation field '{sourceField?.Name}' to relation field '{targetField.Name}'.");
            return;
        }

        if (!string.Equals(sourceReferences, targetField.References, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Action '{actionKey}' step '{stepId}' assigns relation target '{sourceReferences}' to incompatible relation field '{targetField.Name}' targeting '{targetField.References}'.");
        }
    }
}
