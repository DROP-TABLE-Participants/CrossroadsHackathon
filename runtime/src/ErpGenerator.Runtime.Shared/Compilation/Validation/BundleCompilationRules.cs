using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ErpGenerator.Runtime.Shared.Bundles;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleCompilationRules
{
    public static readonly Regex IdentifierPattern = new("^[a-z][a-z0-9_]{1,62}$", RegexOptions.Compiled);
    public static readonly Regex EventNamePattern = new("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$", RegexOptions.Compiled);
    public static readonly Regex RoleIdentifierPattern = new("^[A-Za-z][A-Za-z0-9_]{1,62}$", RegexOptions.Compiled);

    public static readonly HashSet<string> FieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string",
        "text",
        "integer",
        "number",
        "decimal",
        "money",
        "boolean",
        "date",
        "datetime",
        "relation",
        "enum"
    };

    public static readonly HashSet<string> FieldLifecycleTransitions = new(StringComparer.OrdinalIgnoreCase)
    {
        "direct",
        "named_action"
    };

    public static bool IsNamedActionLifecycle(string? transitions) =>
        string.Equals(transitions?.Trim(), "named_action", StringComparison.OrdinalIgnoreCase);

    public static readonly HashSet<string> PermissionActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "*",
        "read",
        "create",
        "update",
        "effect_create",
        "effect_update",
        "archive",
        "restore",
        "admin",
        "start_workflow",
        "approve_task",
        "reject_task",
        "read_task"
    };

    public static readonly HashSet<string> WorkflowStepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "approval",
        "update_record",
        "condition",
        "audit_event",
        "notification_placeholder"
    };

    public static readonly HashSet<string> WorkflowConditionOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq",
        "neq",
        "gt",
        "gte",
        "lt",
        "lte",
        "exists",
        "not_exists",
        "in",
        "not_in",
        "and",
        "or"
    };

    public static readonly HashSet<string> ComputedOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "add",
        "subtract",
        "multiply",
        "divide",
        "concat",
        "coalesce"
    };

    public static readonly HashSet<string> ReportMetricOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "count",
        "sum",
        "avg",
        "min",
        "max"
    };

    public static readonly HashSet<string> ReportFilterOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq",
        "neq",
        "gt",
        "gte",
        "lt",
        "lte",
        "exists",
        "not_exists",
        "in",
        "not_in"
    };

    public const int MaxReportJoins = 3;

    public const int MaxReportJoinDepth = 2;

    public static readonly HashSet<string> ReportJoinTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "left",
        "inner"
    };

    public static readonly HashSet<string> DashboardWidgetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "metric",
        "table"
    };

    public static readonly HashSet<string> ActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "record_action"
    };

    public static readonly HashSet<string> ActionStepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "query_records",
        "guard",
        "create_record",
        "create_records_for_each",
        "update_records",
        "update_current_record",
        "audit_event",
        "return_result"
    };

    public static readonly HashSet<string> ActionAggregateOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "sum"
    };

    public static readonly HashSet<string> ActionExpressionOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "add",
        "subtract",
        "multiply",
        "divide"
    };

    public static readonly HashSet<string> NumericFieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "integer",
        "number",
        "decimal",
        "money",
        "relation"
    };

    public static readonly HashSet<string> ArithmeticFieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "integer",
        "number",
        "decimal",
        "money"
    };

    public static void RequireIdentifier(string value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path} is required.");
            return;
        }

        if (!IdentifierPattern.IsMatch(value))
        {
            errors.Add($"{path} must be lower_snake_case and start with a letter.");
        }
    }

    public static void RequireText(string value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path} is required.");
        }
    }

    public static Dictionary<string, ErpField> BuildFieldLookup(IReadOnlyList<ErpField> fields)
    {
        var lookup = new Dictionary<string, ErpField>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            lookup[field.Name] = field;
            lookup[field.Id] = field;
            if (field.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase))
            {
                lookup[field.Name[..^3]] = field;
            }
        }

        return lookup;
    }

    public static string? ResolveEntityId(
        string entityKey,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity)
    {
        if (string.IsNullOrWhiteSpace(entityKey))
        {
            return null;
        }

        var normalized = entityKey.Trim();
        if (fieldsByEntity.ContainsKey(normalized))
        {
            return normalized;
        }

        var singular = normalized.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^1]
            : normalized;
        return fieldsByEntity.ContainsKey(singular) ? singular : null;
    }

    public static ErpField? ResolveField(
        string entityId,
        string fieldName,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity) =>
        fieldsByEntity.TryGetValue(entityId, out var fields) && fields.TryGetValue(fieldName, out var field)
            ? field
            : null;

    public static bool AreComparableFieldTypes(string leftType, string rightType)
    {
        if (NumericFieldTypes.Contains(leftType) && NumericFieldTypes.Contains(rightType))
        {
            return true;
        }

        if (leftType.Equals("date", StringComparison.OrdinalIgnoreCase) ||
            leftType.Equals("datetime", StringComparison.OrdinalIgnoreCase))
        {
            return rightType.Equals("date", StringComparison.OrdinalIgnoreCase) ||
                   rightType.Equals("datetime", StringComparison.OrdinalIgnoreCase);
        }

        if (leftType.Equals("boolean", StringComparison.OrdinalIgnoreCase))
        {
            return rightType.Equals("boolean", StringComparison.OrdinalIgnoreCase);
        }

        return IsTextType(leftType) && IsTextType(rightType);
    }

    public static bool IsValueCompatible(ErpField field, object? value)
    {
        if (value is null)
        {
            return true;
        }

        return field.Type.ToLowerInvariant() switch
        {
            "integer" or "number" or "decimal" or "money" or "relation" => IsNumericValue(value),
            "boolean" => value is bool || bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out _),
            "date" or "datetime" => value is DateTime or DateTimeOffset || DateTimeOffset.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out _),
            "enum" => field.Options.Count == 0 || field.Options.Contains(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            "string" or "text" => value is not IEnumerable<object?>,
            _ => true
        };
    }

    public static bool IsTextType(string type) =>
        type.Equals("string", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("enum", StringComparison.OrdinalIgnoreCase);

    public static bool IsNumericValue(object? value)
    {
        if (value is null)
        {
            return false;
        }

        return value switch
        {
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => true,
            _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out _)
        };
    }

    public static bool TryGetArrayValue(object? value, out IReadOnlyList<object?> values)
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

    public static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };

    public static IReadOnlyList<ErpField> UniqueFields(IReadOnlyDictionary<string, ErpField> fields) =>
        fields.Values
            .GroupBy(field => field.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    public static Dictionary<string, ErpRole> EffectiveRoles(ErpBundle bundle)
    {
        var roles = new Dictionary<string, ErpRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var (roleId, role) in bundle.Roles)
        {
            roles[roleId] = role;
        }

        return roles;
    }

    public static string NormalizeRoleId(string value) => value.Trim().ToLowerInvariant();

    public static string NormalizePermissionValue(string value) => value.Trim().ToLowerInvariant();

    public static string ToTitleCase(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
}
