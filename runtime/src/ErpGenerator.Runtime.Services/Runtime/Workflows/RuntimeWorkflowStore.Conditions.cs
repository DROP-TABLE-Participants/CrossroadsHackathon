using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeWorkflowStore
{
    private static async Task<RuntimeConditionEvaluation> EvaluateConditionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeEntityDefinition entity,
        long recordId,
        RuntimeWorkflowCondition condition,
        CancellationToken cancellationToken)
    {
        var op = condition.Op.Trim().ToLowerInvariant();
        if (op is "and" or "or")
        {
            var childEvaluations = new List<RuntimeConditionEvaluation>();
            foreach (var child in condition.Conditions)
            {
                childEvaluations.Add(await EvaluateConditionAsync(connection, transaction, entity, recordId, child, cancellationToken));
            }

            var branchResult = op == "and"
                ? childEvaluations.All(child => child.Result)
                : childEvaluations.Any(child => child.Result);
            return new RuntimeConditionEvaluation(branchResult, op);
        }

        var left = await ResolveConditionOperandAsync(connection, transaction, entity, recordId, condition.Left, cancellationToken);
        var right = op is "exists" or "not_exists"
            ? null
            : await ResolveConditionOperandAsync(connection, transaction, entity, recordId, condition.Right, cancellationToken);
        var comparisonResult = op switch
        {
            "eq" => ValuesEqual(left, right),
            "neq" => !ValuesEqual(left, right),
            "gt" => CompareNumeric(left, right) > 0,
            "gte" => CompareNumeric(left, right) >= 0,
            "lt" => CompareNumeric(left, right) < 0,
            "lte" => CompareNumeric(left, right) <= 0,
            "exists" => ValueExists(left),
            "not_exists" => !ValueExists(left),
            "in" => ValueIn(left, right),
            "not_in" => !ValueIn(left, right),
            _ => throw new RuntimeWorkflowValidationException($"Unsupported workflow condition op '{condition.Op}'.")
        };

        return new RuntimeConditionEvaluation(comparisonResult, op);
    }

    private static async Task<object?> ResolveConditionOperandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeEntityDefinition entity,
        long recordId,
        RuntimeWorkflowConditionOperand? operand,
        CancellationToken cancellationToken)
    {
        if (operand is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(operand.Field))
        {
            var field = ResolveField(entity, operand.Field);
            if (field is null)
            {
                throw new RuntimeWorkflowValidationException($"Workflow condition references unknown field '{operand.Field}'.");
            }

            var record = await ReadRecordByIdAsync(connection, entity, recordId, cancellationToken, transaction)
                         ?? throw new RuntimeWorkflowRecordNotFoundException(entity.Key, recordId);
            return record.GetValueOrDefault(field.ColumnName);
        }

        if (!string.IsNullOrWhiteSpace(operand.Path))
        {
            return await ResolveConditionPathAsync(connection, transaction, entity, recordId, operand.Path, cancellationToken);
        }

        return NormalizeConditionValue(operand.Value);
    }

    private static async Task<object?> ResolveConditionPathAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeEntityDefinition startEntity,
        long startRecordId,
        string path,
        CancellationToken cancellationToken)
    {
        var currentEntity = startEntity;
        var currentRecordId = startRecordId;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var field = ResolveField(currentEntity, segments[index]);
            if (field is null)
            {
                throw new RuntimeWorkflowValidationException($"Workflow condition path segment '{segments[index]}' was not found.");
            }

            var record = await ReadRecordByIdAsync(connection, currentEntity, currentRecordId, cancellationToken, transaction)
                         ?? throw new RuntimeWorkflowRecordNotFoundException(currentEntity.Key, currentRecordId);
            if (index == segments.Length - 1)
            {
                return record.GetValueOrDefault(field.ColumnName);
            }

            if (!field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(field.ReferencesEntity))
            {
                throw new RuntimeWorkflowValidationException($"Workflow condition path segment '{segments[index]}' is not a relation field.");
            }

            var rawNextId = record.GetValueOrDefault(field.ColumnName);
            if (rawNextId is null || rawNextId is DBNull)
            {
                return null;
            }

            currentRecordId = Convert.ToInt64(rawNextId, CultureInfo.InvariantCulture);
            currentEntity = await LoadEntityByIdAsync(connection, field.ReferencesEntity, cancellationToken)
                            ?? throw new RuntimeWorkflowValidationException($"Workflow condition path references unknown entity '{field.ReferencesEntity}'.");
        }

        return null;
    }

    private static RuntimeFieldDefinition? ResolveField(RuntimeEntityDefinition entity, string fieldName) =>
        entity.Fields.FirstOrDefault(field =>
            field.Id.Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
            field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
            field.ColumnName.Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
            (field.ColumnName.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
             field.ColumnName[..^3].Equals(fieldName, StringComparison.OrdinalIgnoreCase)));

    private static object? NormalizeConditionValue(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Array => element.EnumerateArray().Select(item => NormalizeConditionValue(item)).ToArray(),
                _ => element.GetRawText()
            };
        }

        return value;
    }

    private static bool ValueExists(object? value) => value is not null and not DBNull;

    private static bool ValuesEqual(object? left, object? right)
    {
        left = NormalizeConditionValue(left);
        right = NormalizeConditionValue(right);
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (TryDecimal(left, out var leftDecimal) && TryDecimal(right, out var rightDecimal))
        {
            return leftDecimal == rightDecimal;
        }

        if (bool.TryParse(Convert.ToString(left, CultureInfo.InvariantCulture), out var leftBool) &&
            bool.TryParse(Convert.ToString(right, CultureInfo.InvariantCulture), out var rightBool))
        {
            return leftBool == rightBool;
        }

        return string.Equals(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareNumeric(object? left, object? right)
    {
        if (!TryDecimal(left, out var leftDecimal) || !TryDecimal(right, out var rightDecimal))
        {
            throw new RuntimeWorkflowValidationException("Workflow condition numeric comparison received a non-numeric value.");
        }

        return leftDecimal.CompareTo(rightDecimal);
    }

    private static bool ValueIn(object? left, object? right)
    {
        right = NormalizeConditionValue(right);
        if (right is IEnumerable<object?> values)
        {
            return values.Any(value => ValuesEqual(left, value));
        }

        return false;
    }

    private static bool TryDecimal(object? value, out decimal result)
    {
        value = NormalizeConditionValue(value);
        return value switch
        {
            null => Assign(0, out result) && false,
            decimal number => Assign(number, out result),
            int number => Assign(number, out result),
            long number => Assign(number, out result),
            double number => Assign(Convert.ToDecimal(number, CultureInfo.InvariantCulture), out result),
            float number => Assign(Convert.ToDecimal(number, CultureInfo.InvariantCulture), out result),
            _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out result)
        };
    }

    private static bool Assign(decimal value, out decimal result)
    {
        result = value;
        return true;
    }

}
