using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ErpGenerator.Runtime.Shared.Bundles;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static partial class BundleScheduleValidator
{
    private static readonly IReadOnlySet<string> SupportedOperators =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "eq", "neq", "gt", "gte", "lt", "lte", "in", "not_in", "exists", "not_exists"
        };

    private static readonly IReadOnlySet<string> OrderedFieldTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "integer", "number", "decimal", "money", "date", "datetime"
        };

    public static void ValidateSchedules(
        ErpBundle bundle,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var scheduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scheduledWorkflowIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workflows = bundle.Modules.SelectMany(module => module.Workflows).ToArray();

        foreach (var schedule in bundle.Schedules)
        {
            var path = $"schedule[{schedule.Id}]";
            RequireIdentifier(schedule.Id, $"{path}.id", errors);
            RejectSqlLikeContent(schedule.Id, $"{path}.id", errors);
            if (!scheduleIds.Add(schedule.Id))
            {
                errors.Add($"Duplicate schedule id '{schedule.Id}'.");
            }

            var exactEntity = fieldsByEntity.Keys.SingleOrDefault(entityId =>
                entityId.Equals(schedule.Entity, StringComparison.Ordinal));
            if (exactEntity is null)
            {
                errors.Add($"Schedule '{schedule.Id}' references unknown or non-canonical entity '{schedule.Entity}'.");
            }

            ErpField? dueField = null;
            if (exactEntity is not null)
            {
                dueField = UniqueFields(fieldsByEntity[exactEntity]).SingleOrDefault(field =>
                    field.Id.Equals(schedule.DueField, StringComparison.Ordinal));
            }

            if (dueField is null)
            {
                errors.Add($"Schedule '{schedule.Id}' due_field '{schedule.DueField}' must be one exact canonical field id on entity '{schedule.Entity}'.");
            }
            else if (!dueField.Type.Equals("datetime", StringComparison.Ordinal) || dueField.Computed is not null)
            {
                errors.Add($"Schedule '{schedule.Id}' due_field '{schedule.Entity}.{schedule.DueField}' must be one materialized datetime field.");
            }

            var matchingWorkflows = workflows.Where(workflow =>
                workflow.Id.Equals(schedule.Workflow, StringComparison.Ordinal)).ToArray();
            if (matchingWorkflows.Length != 1)
            {
                errors.Add($"Schedule '{schedule.Id}' workflow '{schedule.Workflow}' must resolve to exactly one canonical workflow id.");
            }
            else
            {
                var workflow = matchingWorkflows[0];
                if (!workflow.Entity.Equals(schedule.Entity, StringComparison.Ordinal))
                {
                    errors.Add($"Schedule '{schedule.Id}' and workflow '{workflow.Id}' must target the same exact entity.");
                }

                if (workflow.Trigger is null ||
                    !string.Equals(workflow.Trigger.Action, string.Empty, StringComparison.Ordinal))
                {
                    errors.Add($"Schedule '{schedule.Id}' workflow '{workflow.Id}' must be triggerless; scheduled workflows cannot declare trigger.action.");
                }

                if (!scheduledWorkflowIds.Add(workflow.Id))
                {
                    errors.Add($"Workflow '{workflow.Id}' must be targeted by at most one schedule.");
                }
            }

            if (schedule.Filters.Count > ErpScheduleLimits.MaxFilters)
            {
                errors.Add($"Schedule '{schedule.Id}' cannot declare more than {ErpScheduleLimits.MaxFilters} filters.");
            }

            var filterIdentities = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < schedule.Filters.Count; index++)
            {
                ValidateFilter(
                    schedule,
                    schedule.Filters[index],
                    index,
                    exactEntity is null ? null : fieldsByEntity[exactEntity],
                    filterIdentities,
                    errors);
            }
        }
    }

    private static void ValidateFilter(
        ErpSchedule schedule,
        ErpScheduleFilter filter,
        int index,
        IReadOnlyDictionary<string, ErpField>? fields,
        HashSet<string> filterIdentities,
        List<string> errors)
    {
        var path = $"schedule[{schedule.Id}].filters[{index}]";
        RejectSqlLikeContent(filter.Field, $"{path}.field", errors);
        RejectSqlLikeContent(filter.Op, $"{path}.op", errors);

        var field = fields is null
            ? null
            : UniqueFields(fields).SingleOrDefault(candidate =>
                candidate.Id.Equals(filter.Field, StringComparison.Ordinal));
        if (field is null)
        {
            errors.Add($"Schedule '{schedule.Id}' filter field '{filter.Field}' must be one exact canonical field id on entity '{schedule.Entity}'.");
        }
        else if (field.Computed is not null)
        {
            errors.Add($"Schedule '{schedule.Id}' filter field '{schedule.Entity}.{filter.Field}' must be materialized on the same record.");
        }

        if (!SupportedOperators.Contains(filter.Op))
        {
            errors.Add($"Schedule '{schedule.Id}' filter '{filter.Field}' uses unsupported operator '{filter.Op}'.");
        }
        else if (field is not null && filter.Op is "gt" or "gte" or "lt" or "lte" &&
                 !OrderedFieldTypes.Contains(field.Type))
        {
            errors.Add($"Schedule '{schedule.Id}' filter '{filter.Field}' operator '{filter.Op}' requires an ordered materialized field.");
        }

        var identity = $"{filter.Field}\u001f{filter.Op}";
        if (!filterIdentities.Add(identity))
        {
            errors.Add($"Schedule '{schedule.Id}' contains duplicate filter '{filter.Field} {filter.Op}'.");
        }

        if (filter.Op is "exists" or "not_exists")
        {
            if (filter.Value is not null || filter.Values.Count != 0)
            {
                errors.Add($"Schedule '{schedule.Id}' filter '{filter.Field}' operator '{filter.Op}' cannot declare value or values.");
            }

            return;
        }

        if (filter.Op is "in" or "not_in")
        {
            if (filter.Value is not null || filter.Values.Count is < 1 or > ErpScheduleLimits.MaxValuesPerFilter)
            {
                errors.Add($"Schedule '{schedule.Id}' filter '{filter.Field}' operator '{filter.Op}' must declare between 1 and {ErpScheduleLimits.MaxValuesPerFilter} typed values and no scalar value.");
            }

            if (field is not null)
            {
                var valueIdentities = new HashSet<string>(StringComparer.Ordinal);
                foreach (var literal in filter.Values)
                {
                    ValidateLiteral(schedule, field, literal, errors);
                    if (!valueIdentities.Add($"{literal.Type}\u001f{LiteralText(literal.Value ?? string.Empty)}"))
                    {
                        errors.Add($"Schedule '{schedule.Id}' filter '{filter.Field}' contains a duplicate typed value.");
                    }
                }
            }

            return;
        }

        if (filter.Value is null || filter.Values.Count != 0)
        {
            errors.Add($"Schedule '{schedule.Id}' filter '{filter.Field}' operator '{filter.Op}' must declare exactly one typed value.");
            return;
        }

        if (field is not null)
        {
            ValidateLiteral(schedule, field, filter.Value, errors);
        }
    }

    private static void ValidateLiteral(
        ErpSchedule schedule,
        ErpField field,
        ErpTypedLiteral literal,
        List<string> errors)
    {
        var expectedType = ExpectedLiteralType(field.Type);
        var path = $"Schedule '{schedule.Id}' filter '{field.Id}'";
        if (!string.Equals(literal.Type, expectedType, StringComparison.Ordinal))
        {
            errors.Add($"{path} literal type must be exactly '{expectedType}' for field type '{field.Type}'.");
            return;
        }

        if (literal.Value is null || !IsLiteralValueValid(field, expectedType, literal.Value))
        {
            errors.Add($"{path} literal value must exactly match type '{expectedType}' and the field's canonical options.");
        }
    }

    private static string ExpectedLiteralType(string fieldType) => fieldType.ToLowerInvariant() switch
    {
        "string" or "text" or "enum" => "string",
        "integer" or "relation" => "integer",
        "number" or "decimal" or "money" => "decimal",
        "boolean" => "boolean",
        "date" => "date",
        "datetime" => "datetime",
        _ => string.Empty
    };

    private static bool IsLiteralValueValid(ErpField field, string type, object value)
    {
        var text = LiteralText(value);
        return type switch
        {
            "string" => IsStringLiteral(value) &&
                        (!field.Type.Equals("enum", StringComparison.OrdinalIgnoreCase) ||
                         field.Options.Contains(text, StringComparer.Ordinal)),
            "integer" => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "decimal" => decimal.TryParse(
                text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out _),
            "boolean" => value is bool || bool.TryParse(text, out _),
            "date" => value is DateOnly || DateOnly.TryParseExact(
                text,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            "datetime" => value is DateTimeOffset ||
                          ExplicitOffsetPattern().IsMatch(text) && DateTimeOffset.TryParse(
                              text,
                              CultureInfo.InvariantCulture,
                              DateTimeStyles.RoundtripKind,
                              out _),
            _ => false
        };
    }

    private static bool IsStringLiteral(object value) =>
        value is string || value is JsonElement { ValueKind: JsonValueKind.String };

    private static string LiteralText(object value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    [GeneratedRegex("(?:[zZ]|[+-][0-9]{2}:[0-9]{2})$")]
    private static partial Regex ExplicitOffsetPattern();
}
