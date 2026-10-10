using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeRecordStore
{
    private static void ApplyComputedFields(
        RuntimeEntityDefinition entity,
        IReadOnlyList<Dictionary<string, object?>> records)
    {
        var computedFields = entity.Fields.Where(field => field.Computed is not null).ToArray();
        if (computedFields.Length == 0 || records.Count == 0)
        {
            return;
        }

        var fieldsByKey = entity.Fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            foreach (var field in computedFields)
            {
                record[field.ColumnName] = EvaluateComputedField(field, fieldsByKey, record);
            }
        }
    }

    private static object? EvaluateComputedField(
        RuntimeFieldDefinition field,
        IReadOnlyDictionary<string, RuntimeFieldDefinition> fieldsByKey,
        IReadOnlyDictionary<string, object?> record)
    {
        var computed = field.Computed;
        if (computed is null)
        {
            return null;
        }

        var left = ResolveComputedOperand(computed.Left, fieldsByKey, record);
        var right = ResolveComputedOperand(computed.Right, fieldsByKey, record);
        return computed.Op switch
        {
            "add" => ComputeNumeric(left, right, (leftNumber, rightNumber) => leftNumber + rightNumber),
            "subtract" => ComputeNumeric(left, right, (leftNumber, rightNumber) => leftNumber - rightNumber),
            "multiply" => ComputeNumeric(left, right, (leftNumber, rightNumber) => leftNumber * rightNumber),
            "divide" => ComputeDivide(left, right),
            "concat" => $"{FormatComputedValue(left)}{FormatComputedValue(right)}",
            "coalesce" => IsNullLike(left) ? right : left,
            _ => null
        };
    }

    private static decimal? ComputeNumeric(object? left, object? right, Func<decimal, decimal, decimal> operation) =>
        TryDecimal(left, out var leftNumber) && TryDecimal(right, out var rightNumber)
            ? operation(leftNumber, rightNumber)
            : null;

    private static decimal? ComputeDivide(object? left, object? right) =>
        TryDecimal(left, out var leftNumber) && TryDecimal(right, out var rightNumber) && rightNumber != 0
            ? leftNumber / rightNumber
            : null;

    private static string FormatComputedValue(object? value) =>
        value switch
        {
            null => string.Empty,
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

    private static object? ResolveComputedOperand(
        RuntimeComputedOperand operand,
        IReadOnlyDictionary<string, RuntimeFieldDefinition> fieldsByKey,
        IReadOnlyDictionary<string, object?> record)
    {
        if (!string.IsNullOrWhiteSpace(operand.Field) && fieldsByKey.TryGetValue(operand.Field, out var field))
        {
            return record.GetValueOrDefault(field.ColumnName);
        }

        return operand.Value;
    }

    private static bool TryDecimal(object? value, out decimal result)
    {
        if (value is decimal decimalValue)
        {
            result = decimalValue;
            return true;
        }

        if (value is int or long or short or byte or float or double)
        {
            return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
        }

        return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static bool IsNullLike(object? value) =>
        value is null || (value is string text && string.IsNullOrWhiteSpace(text));

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
}
