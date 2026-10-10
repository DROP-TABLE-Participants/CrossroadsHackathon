using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeRecordStore
{
    private async Task<Dictionary<string, object?>> ValidateInputAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, JsonElement> values,
        bool isCreate,
        CancellationToken cancellationToken)
    {
        var errors = new List<RuntimeValidationError>();
        var computedByKey = entity.Fields
            .Where(field => field.Computed is not null)
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var writableByKey = entity.Fields
            .Where(field => field.Computed is null)
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var converted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in values.Keys)
        {
            if (key is "id" or "created_at" or "created_by" or "updated_at" or "updated_by" or "archived_at" or "archived_by" or "archive_reason")
            {
                errors.Add(new RuntimeValidationError(key, "System fields are read-only."));
                continue;
            }

            if (computedByKey.ContainsKey(key))
            {
                errors.Add(new RuntimeValidationError(key, $"Computed field '{key}' is read-only."));
            }
            else if (!writableByKey.TryGetValue(key, out var field))
            {
                errors.Add(new RuntimeValidationError(key, $"Unknown field '{key}'."));
            }
            else if (isCreate && field.Lifecycle is not null)
            {
                errors.Add(new RuntimeValidationError(
                    key,
                    $"{field.DisplayName} is initialized by the runtime and cannot be supplied on create."));
            }
            else if (!isCreate && field.Lifecycle?.RequiresNamedAction == true)
            {
                var prefix = $"{entity.Id}.";
                var canonicalField = field.Id.StartsWith(prefix, StringComparison.Ordinal)
                    ? field.Id[prefix.Length..]
                    : field.Name;
                throw new RuntimeLifecycleTransitionDeniedException(
                    entity.Id,
                    canonicalField,
                    field.DisplayName);
            }
            else if (!permissions.IsFieldAllowed(snapshot, "write", entity, field.Name))
            {
                throw new RuntimeRecordPermissionException("field.write", entity.Key);
            }
        }

        if (isCreate)
        {
            foreach (var field in entity.Fields.Where(field =>
                         field.Required && field.Computed is null && field.Lifecycle is null))
            {
                if (!values.TryGetValue(field.Name, out var value) || IsEmpty(value))
                {
                    errors.Add(new RuntimeValidationError(field.Name, $"{field.DisplayName} is required."));
                }
            }
        }

        foreach (var (key, value) in values)
        {
            if (!writableByKey.TryGetValue(key, out var field) ||
                field.Lifecycle is not null && (isCreate || field.Lifecycle.RequiresNamedAction))
            {
                continue;
            }

            if (IsEmpty(value))
            {
                converted[field.ColumnName] = null;
                continue;
            }

            if (!TryConvertValue(field, value, out var convertedValue, out var error))
            {
                errors.Add(new RuntimeValidationError(field.Name, error));
                continue;
            }

            if (field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) &&
                field.ReferencesEntity is not null &&
                convertedValue is long relatedId &&
                !(isCreate && RuntimeRelationCreateGuardEnforcer.IsGuardedRelation(entity, field)) &&
                !await RelationExistsAsync(
                    connection,
                    transaction,
                    snapshot,
                    user,
                    field.ReferencesEntity,
                    relatedId,
                    cancellationToken))
            {
                errors.Add(new RuntimeValidationError(field.Name, $"{field.DisplayName} references a missing record."));
                continue;
            }

            converted[field.ColumnName] = convertedValue;
        }

        if (isCreate)
        {
            foreach (var field in entity.Fields.Where(field => field.Lifecycle is not null))
            {
                converted[field.ColumnName] = field.Lifecycle!.Initial;
            }
        }

        if (errors.Count > 0)
        {
            throw new RuntimeRecordValidationException(errors);
        }

        return converted;
    }

    private static bool TryConvertValue(RuntimeFieldDefinition field, JsonElement value, out object? result, out string error)
    {
        result = null;
        error = string.Empty;

        try
        {
            switch (field.FieldType.ToLowerInvariant())
            {
                case "string":
                case "text":
                    result = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : value.ToString();
                    return true;
                case "enum":
                    var enumValue = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : value.ToString();
                    if (string.IsNullOrWhiteSpace(enumValue) || !field.Options.Contains(enumValue, StringComparer.OrdinalIgnoreCase))
                    {
                        error = $"{field.DisplayName} must be one of: {string.Join(", ", field.Options)}.";
                        return false;
                    }

                    result = field.Options.First(option => option.Equals(enumValue, StringComparison.OrdinalIgnoreCase));
                    return true;
                case "integer":
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer))
                    {
                        result = integer;
                        return true;
                    }

                    if (long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                    {
                        result = integer;
                        return true;
                    }

                    error = $"{field.DisplayName} must be an integer.";
                    return false;
                case "number":
                case "decimal":
                case "money":
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
                    {
                        result = number;
                        return true;
                    }

                    if (decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
                    {
                        result = number;
                        return true;
                    }

                    error = $"{field.DisplayName} must be numeric.";
                    return false;
                case "boolean":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        result = value.GetBoolean();
                        return true;
                    }

                    if (bool.TryParse(value.GetString(), out var boolean))
                    {
                        result = boolean;
                        return true;
                    }

                    error = $"{field.DisplayName} must be true or false.";
                    return false;
                case "date":
                    if (DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        result = date;
                        return true;
                    }

                    error = $"{field.DisplayName} must be a valid date.";
                    return false;
                case "datetime":
                    if (DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime))
                    {
                        result = dateTime;
                        return true;
                    }

                    error = $"{field.DisplayName} must be a valid date/time.";
                    return false;
                case "relation":
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id))
                    {
                        result = id;
                        return true;
                    }

                    if (long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                    {
                        result = id;
                        return true;
                    }

                    error = $"{field.DisplayName} must be a record ID.";
                    return false;
                default:
                    error = $"{field.DisplayName} uses unsupported type '{field.FieldType}'.";
                    return false;
            }
        }
        catch (InvalidOperationException)
        {
            error = $"{field.DisplayName} has an invalid value.";
            return false;
        }
    }

    private static bool IsEmpty(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
        (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));
}
