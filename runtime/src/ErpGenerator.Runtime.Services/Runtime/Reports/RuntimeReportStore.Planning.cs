using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeReportStore
{
    private static string ResolveMetricValueType(
        RuntimeReportMetric metric,
        IReadOnlyDictionary<string, RuntimeReportFieldContext> contexts) =>
        metric.Op.Trim().ToLowerInvariant() switch
        {
            "count" => "integer",
            "sum" or "avg" => "decimal",
            "min" or "max" when !string.IsNullOrWhiteSpace(metric.Field) =>
                ResolveReportField(metric.Field, contexts).Field.FieldType,
            _ => string.Empty
        };

    private static string BuildMetricExpression(
        RuntimeReportMetric metric,
        IReadOnlyDictionary<string, RuntimeReportFieldContext> contexts,
        List<NpgsqlParameter> parameters)
    {
        var op = metric.Op.ToLowerInvariant();
        var fieldExpression = "*";
        if (!string.IsNullOrWhiteSpace(metric.Field))
        {
            var resolved = ResolveReportField(metric.Field, contexts);
            fieldExpression = BuildFieldExpression(
                resolved.Field,
                resolved.Context.Fields,
                parameters,
                resolved.Context.SqlAlias);
        }

        var filterSql = metric.Filter is null
            ? string.Empty
            : $" filter (where {BuildFilterExpression(metric.Filter, contexts, parameters)})";
        return op switch
        {
            "count" => $"count({fieldExpression}){filterSql}",
            "sum" => $"coalesce(sum({fieldExpression}){filterSql}, 0)",
            "avg" => $"avg({fieldExpression}){filterSql}",
            "min" => $"min({fieldExpression}){filterSql}",
            "max" => $"max({fieldExpression}){filterSql}",
            _ => throw new InvalidOperationException($"Unsupported metric op '{metric.Op}'.")
        };
    }

    private static string BuildFilterExpression(
        RuntimeReportFilter filter,
        IReadOnlyDictionary<string, RuntimeReportFieldContext> contexts,
        List<NpgsqlParameter> parameters)
    {
        var resolved = ResolveReportField(filter.Field, contexts);
        var field = resolved.Field;
        var expression = BuildFieldExpression(field, resolved.Context.Fields, parameters, resolved.Context.SqlAlias);
        return filter.Op.ToLowerInvariant() switch
        {
            "eq" => $"{expression} = {AddParameter(parameters, ConvertFilterValue(field, filter.Value))}",
            "neq" => $"{expression} <> {AddParameter(parameters, ConvertFilterValue(field, filter.Value))}",
            "gt" => $"{expression} > {AddParameter(parameters, ConvertFilterValue(field, filter.Value))}",
            "gte" => $"{expression} >= {AddParameter(parameters, ConvertFilterValue(field, filter.Value))}",
            "lt" => $"{expression} < {AddParameter(parameters, ConvertFilterValue(field, filter.Value))}",
            "lte" => $"{expression} <= {AddParameter(parameters, ConvertFilterValue(field, filter.Value))}",
            "exists" => $"{expression} is not null",
            "not_exists" => $"{expression} is null",
            "in" => BuildInExpression(expression, field, filter.Values.Count > 0 ? filter.Values : ToObjectArray(filter.Value), parameters, negate: false),
            "not_in" => BuildInExpression(expression, field, filter.Values.Count > 0 ? filter.Values : ToObjectArray(filter.Value), parameters, negate: true),
            _ => throw new InvalidOperationException($"Unsupported filter op '{filter.Op}'.")
        };
    }

    private static string BuildInExpression(
        string expression,
        RuntimeFieldDefinition field,
        IReadOnlyList<object?> values,
        List<NpgsqlParameter> parameters,
        bool negate)
    {
        if (values.Count == 0)
        {
            return negate ? "true" : "false";
        }

        var parameterNames = values
            .Select(value => AddParameter(parameters, ConvertFilterValue(field, value)))
            .ToArray();
        var op = negate ? "not in" : "in";
        return $"{expression} {op} ({string.Join(", ", parameterNames)})";
    }

    private static string BuildFieldExpression(
        RuntimeFieldDefinition field,
        IReadOnlyDictionary<string, RuntimeFieldDefinition> fieldsByKey,
        List<NpgsqlParameter> parameters,
        string sqlAlias)
    {
        if (field.Computed is null)
        {
            return $"{sqlAlias}.{Quote(field.ColumnName)}";
        }

        var computed = field.Computed;
        var left = BuildComputedOperandExpression(computed.Left, fieldsByKey, parameters, sqlAlias);
        var right = BuildComputedOperandExpression(computed.Right, fieldsByKey, parameters, sqlAlias);
        return computed.Op switch
        {
            "add" => $"(({left}) + ({right}))",
            "subtract" => $"(({left}) - ({right}))",
            "multiply" => $"(({left}) * ({right}))",
            "divide" => $"case when ({right}) is null or ({right}) = 0 then null else ({left}) / ({right}) end",
            "concat" => $"(coalesce(({left})::text, '') || coalesce(({right})::text, ''))",
            "coalesce" => $"coalesce(({left}), ({right}))",
            _ => throw new InvalidOperationException($"Unsupported computed op '{computed.Op}'.")
        };
    }

    private static string BuildComputedOperandExpression(
        RuntimeComputedOperand operand,
        IReadOnlyDictionary<string, RuntimeFieldDefinition> fieldsByKey,
        List<NpgsqlParameter> parameters,
        string sqlAlias)
    {
        if (!string.IsNullOrWhiteSpace(operand.Field))
        {
            var field = ResolveField(fieldsByKey, operand.Field)
                        ?? throw new InvalidOperationException($"Computed operand field '{operand.Field}' was not found.");
            return BuildFieldExpression(field, fieldsByKey, parameters, sqlAlias);
        }

        return AddParameter(parameters, operand.Value);
    }

    private static void AddDateWindowFilters(
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, RuntimeFieldDefinition> fieldsByKey,
        RuntimeReportQuery query,
        List<string> whereParts,
        List<NpgsqlParameter> parameters)
    {
        if (query.DateFrom is null && query.DateTo is null)
        {
            return;
        }

        var field = ResolveDateWindowField(entity);
        if (field is null)
        {
            return;
        }

        var expression = field == CreatedAtSystemField
            ? "t.\"created_at\""
            : BuildFieldExpression(field, fieldsByKey, parameters, "t");
        if (query.DateFrom is not null)
        {
            whereParts.Add($"{expression} >= {AddParameter(parameters, query.DateFrom.Value)}");
        }

        if (query.DateTo is not null)
        {
            whereParts.Add($"{expression} <= {AddParameter(parameters, query.DateTo.Value)}");
        }
    }

    private static RuntimeFieldDefinition? ResolveDateWindowField(RuntimeEntityDefinition entity)
    {
        var candidates = new[] { "date", "logged_on", "issued_on", "due_date" };
        foreach (var candidate in candidates)
        {
            var field = entity.Fields.FirstOrDefault(field =>
                field.Computed is null &&
                field.FieldType is "date" or "datetime" &&
                (field.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                 field.ColumnName.Equals(candidate, StringComparison.OrdinalIgnoreCase)));
            if (field is not null)
            {
                return field;
            }
        }

        return CreatedAtSystemField;
    }

}
