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
    private async Task<Dictionary<string, object?>> ResolveValuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, ActionValueDefinition> definitions,
        RuntimeActionContext context,
        IReadOnlyDictionary<string, object?>? item,
        RuntimeEntityDefinition? itemEntity,
        bool requireRequiredFields,
        string mutationAction,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldName, definition) in definitions)
        {
            var field = ResolveField(entity, fieldName)
                        ?? throw new RuntimeActionValidationException($"Action references unknown field '{fieldName}' on {entity.Key}.");
            if (field.Computed is not null)
            {
                throw new RuntimeActionValidationException($"Action cannot set computed field '{fieldName}'.");
            }

            if (requireRequiredFields && field.Lifecycle is not null)
            {
                throw new RuntimeActionValidationException(
                    $"Action cannot supply server-initialized lifecycle field '{entity.Key}.{field.Name}' while creating a record.");
            }

            if (!requireRequiredFields &&
                field.Lifecycle?.RequiresNamedAction == true &&
                !mutationAction.Equals(ActionPermissionEffectAnalyzer.EffectUpdate, StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeActionValidationException(
                    $"Named-action lifecycle field '{entity.Key}.{field.Name}' requires effect_update authority.");
            }

            EnsureActionFieldAllowed(
                permissionSnapshot,
                mutationAction.StartsWith("effect_", StringComparison.OrdinalIgnoreCase)
                    ? "effect_write"
                    : "write",
                entity,
                field.Name);
            EnsureValueSourceFieldsAllowed(permissionSnapshot, definition, context, itemEntity);
            var value = ResolveValue(definition, context, item);
            values[field.ColumnName] = await ConvertValueAsync(
                connection,
                transaction,
                permissionSnapshot,
                user,
                entity,
                field,
                value,
                enforceRelationCreateGuards: requireRequiredFields,
                cancellationToken);
        }

        foreach (var field in entity.Fields.Where(field =>
                     requireRequiredFields &&
                     field.Required &&
                     field.Computed is null &&
                     field.Lifecycle is null))
        {
            if (!values.ContainsKey(field.ColumnName) || IsNullLike(values[field.ColumnName]))
            {
                throw new RuntimeActionValidationException($"{field.DisplayName} is required.");
            }
        }

        return values;
    }

    private object? ResolveValue(
        ActionValueDefinition definition,
        RuntimeActionContext context,
        IReadOnlyDictionary<string, object?>? item)
    {
        if (definition.ValueFrom is not null)
        {
            return ResolveValueFrom(definition.ValueFrom, context, item);
        }

        if (!string.IsNullOrWhiteSpace(definition.Template))
        {
            return RenderTemplate(definition.Template!, context, item);
        }

        if (definition.Aggregate is not null)
        {
            return ResolveAggregate(definition.Aggregate, context);
        }

        return JsonElementToObject(definition.Value);
    }

    private static object? ResolveValueFrom(
        ActionValueFromDefinition valueFrom,
        RuntimeActionContext context,
        IReadOnlyDictionary<string, object?>? item)
    {
        if (!string.IsNullOrWhiteSpace(valueFrom.Record))
        {
            return ResolveRecordValue(context.SourceEntity, context.SourceRecord, valueFrom.Record!);
        }

        if (!string.IsNullOrWhiteSpace(valueFrom.Item))
        {
            if (item is null)
            {
                throw new RuntimeActionValidationException("value_from.item was used outside an item context.");
            }

            return ResolveRawRecordValue(item, valueFrom.Item!);
        }

        if (!string.IsNullOrWhiteSpace(valueFrom.Step))
        {
            if (!context.CreatedRecords.TryGetValue(valueFrom.Step!, out var created))
            {
                throw new RuntimeActionValidationException($"value_from.step references unknown step '{valueFrom.Step}'.");
            }

            return ResolveRecordValue(created.Entity, created.Record, string.IsNullOrWhiteSpace(valueFrom.Field) ? "id" : valueFrom.Field!);
        }

        if (!string.IsNullOrWhiteSpace(valueFrom.Now))
        {
            return valueFrom.Now!.Trim().ToLowerInvariant() switch
            {
                "date" => DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime),
                "datetime" or "timestamp" => DateTimeOffset.UtcNow,
                _ => throw new RuntimeActionValidationException($"Unsupported now value '{valueFrom.Now}'.")
            };
        }

        return null;
    }

    private static object? ResolveAggregate(ActionAggregateDefinition aggregate, RuntimeActionContext context)
    {
        var query = ResolveQueryResult(context, aggregate.Result, aggregate.Result);
        if (!aggregate.Op.Equals("sum", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeActionValidationException($"Unsupported aggregate op '{aggregate.Op}'.");
        }

        decimal sum = 0;
        foreach (var item in query.Records)
        {
            var value = EvaluateExpression(aggregate.Expression, context, item);
            if (TryDecimal(value, out var number))
            {
                sum += number;
            }
        }

        return sum;
    }

    private static object? EvaluateExpression(
        ActionExpressionDefinition? expression,
        RuntimeActionContext context,
        IReadOnlyDictionary<string, object?> item)
    {
        if (expression is null)
        {
            return null;
        }

        var left = ResolveOperand(expression.Left, context, item);
        var right = ResolveOperand(expression.Right, context, item);
        if (!TryDecimal(left, out var leftNumber) || !TryDecimal(right, out var rightNumber))
        {
            return null;
        }

        return expression.Op switch
        {
            "add" => leftNumber + rightNumber,
            "subtract" => leftNumber - rightNumber,
            "multiply" => leftNumber * rightNumber,
            "divide" when rightNumber != 0 => leftNumber / rightNumber,
            "divide" => null,
            _ => throw new RuntimeActionValidationException($"Unsupported expression op '{expression.Op}'.")
        };
    }

    private static bool EvaluateVisibleWhen(
        ActionDefinition action,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> record)
    {
        if (action.VisibleWhen.Count == 0)
        {
            return true;
        }

        var context = new RuntimeActionContext(entity, record);
        return action.VisibleWhen.All(condition => EvaluateCondition(condition, context, null));
    }

    private static bool EvaluateCondition(
        ActionConditionDefinition condition,
        RuntimeActionContext context,
        IReadOnlyDictionary<string, object?>? item)
    {
        var op = condition.Op.Trim().ToLowerInvariant();
        if (op == "and")
        {
            return condition.Conditions.All(child => EvaluateCondition(child, context, item));
        }

        if (op == "or")
        {
            return condition.Conditions.Any(child => EvaluateCondition(child, context, item));
        }

        var left = ResolveOperand(condition.Left, context, item);
        if (op == "exists")
        {
            return !IsNullLike(left);
        }

        if (op == "not_exists")
        {
            return IsNullLike(left);
        }

        var right = ResolveOperand(condition.Right, context, item);
        var comparison = CompareValues(left, right);
        return op switch
        {
            "eq" => ValuesEqual(left, right),
            "neq" => !ValuesEqual(left, right),
            "gt" => comparison > 0,
            "gte" => comparison >= 0,
            "lt" => comparison < 0,
            "lte" => comparison <= 0,
            "in" => ValueIn(left, right, negate: false),
            "not_in" => ValueIn(left, right, negate: true),
            _ => throw new RuntimeActionValidationException($"Unsupported condition op '{condition.Op}'.")
        };
    }

    private static object? ResolveOperand(
        ActionOperandDefinition? operand,
        RuntimeActionContext context,
        IReadOnlyDictionary<string, object?>? item)
    {
        if (operand is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(operand.ResultCount))
        {
            return context.Counts.TryGetValue(operand.ResultCount!, out var count)
                ? count
                : throw new RuntimeActionValidationException($"Unknown action result '{operand.ResultCount}'.");
        }

        if (!string.IsNullOrWhiteSpace(operand.Field))
        {
            return ResolveRecordValue(context.SourceEntity, context.SourceRecord, operand.Field!);
        }

        if (operand.ValueFrom is not null)
        {
            return ResolveValueFrom(operand.ValueFrom, context, item);
        }

        return JsonElementToObject(operand.Value);
    }

    private static string RenderTemplate(
        string template,
        RuntimeActionContext context,
        IReadOnlyDictionary<string, object?>? item)
    {
        return TemplateTokenPattern.Replace(template, match =>
        {
            var token = match.Groups[1].Value;
            object? value = token switch
            {
                "now_date" => DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "now_timestamp" => DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                _ when token.StartsWith("record.", StringComparison.OrdinalIgnoreCase) =>
                    ResolveRecordValue(context.SourceEntity, context.SourceRecord, token["record.".Length..]),
                _ when token.StartsWith("item.", StringComparison.OrdinalIgnoreCase) && item is not null =>
                    ResolveRawRecordValue(item, token["item.".Length..]),
                _ when token.StartsWith("step.", StringComparison.OrdinalIgnoreCase) =>
                    ResolveStepTemplateValue(context, token),
                _ => match.Value
            };

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        });
    }

    private static object? ResolveStepTemplateValue(RuntimeActionContext context, string token)
    {
        var segments = token.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3)
        {
            return null;
        }

        if (!context.CreatedRecords.TryGetValue(segments[1], out var created))
        {
            return null;
        }

        return ResolveRecordValue(created.Entity, created.Record, segments[2]);
    }

    private static RuntimeFieldDefinition? ResolveField(RuntimeEntityDefinition entity, string fieldKey)
    {
        if (string.IsNullOrWhiteSpace(fieldKey))
        {
            return null;
        }

        return entity.Fields.FirstOrDefault(field =>
            field.Id.Equals(fieldKey, StringComparison.OrdinalIgnoreCase) ||
            field.Name.Equals(fieldKey, StringComparison.OrdinalIgnoreCase) ||
            field.ColumnName.Equals(fieldKey, StringComparison.OrdinalIgnoreCase) ||
            (field.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
             field.Name[..^3].Equals(fieldKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static object? ResolveRecordValue(
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> record,
        string fieldKey)
    {
        if (fieldKey.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            return record.GetValueOrDefault("id");
        }

        var field = ResolveField(entity, fieldKey);
        return field is null ? ResolveRawRecordValue(record, fieldKey) : record.GetValueOrDefault(field.ColumnName);
    }

    private static object? ResolveRawRecordValue(IReadOnlyDictionary<string, object?> record, string fieldKey)
    {
        if (record.TryGetValue(fieldKey, out var value))
        {
            return value;
        }

        var idKey = fieldKey.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ? fieldKey[..^3] : $"{fieldKey}_id";
        return record.TryGetValue(idKey, out value) ? value : null;
    }

    private static RuntimeActionQueryResult ResolveQueryResult(RuntimeActionContext context, string? result, string stepId)
    {
        if (string.IsNullOrWhiteSpace(result) || !context.QueryResults.TryGetValue(result, out var query))
        {
            throw new RuntimeActionValidationException($"Action step '{stepId}' references unknown query result '{result}'.");
        }

        return query;
    }

    private static int CompareValues(object? left, object? right)
    {
        if (TryDecimal(left, out var leftNumber) && TryDecimal(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (DateTimeOffset.TryParse(Convert.ToString(left, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var leftDate) &&
            DateTimeOffset.TryParse(Convert.ToString(right, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rightDate))
        {
            return leftDate.CompareTo(rightDate);
        }

        return string.Compare(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (IsNullLike(left) && IsNullLike(right))
        {
            return true;
        }

        if (TryDecimal(left, out var leftNumber) && TryDecimal(right, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return string.Equals(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValueIn(object? left, object? right, bool negate)
    {
        var values = TryGetArrayValue(right, out var array) ? array : [right];
        var contains = values.Any(value => ValuesEqual(left, value));
        return negate ? !contains : contains;
    }

    private static bool TryGetArrayValue(object? value, out IReadOnlyList<object?> values)
    {
        switch (value)
        {
            case null:
                values = [];
                return false;
            case object?[] array:
                values = array;
                return true;
            case IReadOnlyList<object?> list:
                values = list;
                return true;
            case IEnumerable<object?> enumerable when value is not string:
                values = enumerable.ToArray();
                return true;
            case JsonElement { ValueKind: JsonValueKind.Array } element:
                values = element.EnumerateArray().Select(JsonElementToObject).ToArray();
                return true;
            default:
                values = [];
                return false;
        }
    }

    private static object? JsonElementToObject(object? value) =>
        value is JsonElement element ? JsonElementToObject(element) : value;

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToArray(),
        _ => element.GetRawText()
    };

    private static bool TryDecimal(object? value, out decimal result)
    {
        if (value is decimal decimalValue)
        {
            result = decimalValue;
            return true;
        }

        return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static long? ToLong(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is long longValue)
        {
            return longValue;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsNullLike(object? value) =>
        value is null or DBNull || (value is string text && string.IsNullOrWhiteSpace(text));
}
