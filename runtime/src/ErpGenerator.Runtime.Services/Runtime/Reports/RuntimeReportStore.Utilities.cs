using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeReportStore
{
    private static object? ConvertFilterValue(RuntimeFieldDefinition field, object? value)
    {
        value = JsonElementToObject(value);
        if (value is null)
        {
            return DBNull.Value;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return field.FieldType.ToLowerInvariant() switch
        {
            "integer" or "relation" => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer : value,
            "number" or "decimal" or "money" => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : value,
            "boolean" => bool.TryParse(text, out var boolean) ? boolean : value,
            "date" => DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : value,
            "datetime" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime) ? dateTime : value,
            _ => text
        };
    }

    private static IReadOnlyList<object?> ToObjectArray(object? value)
    {
        value = JsonElementToObject(value);
        return value switch
        {
            null => [],
            object?[] array => array,
            IReadOnlyList<object?> list => list,
            IEnumerable<object?> enumerable when value is not string => enumerable.ToArray(),
            _ => [value]
        };
    }

    private static object? JsonElementToObject(object? value)
    {
        if (value is not JsonElement element)
        {
            return value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(item => JsonElementToObject(item)).ToArray(),
            _ => element.GetRawText()
        };
    }

    private static string AddParameter(List<NpgsqlParameter> parameters, object? value)
    {
        var name = $"p{parameters.Count}";
        parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
        return $"@{name}";
    }

    private static long? ToLong(object? value)
    {
        value = JsonElementToObject(value);
        return value switch
        {
            null => null,
            long longValue => longValue,
            int intValue => intValue,
            _ => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
        };
    }

    private static object NormalizeValue(object value) => value switch
    {
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime,
        DateTimeOffset dateTimeOffset => dateTimeOffset,
        decimal number => number,
        _ => value
    };

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private async Task<NpgsqlConnection> OpenConnectionAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void EnsureIdentifier(string value)
    {
        if (!IdentifierPattern.IsMatch(value))
        {
            throw new InvalidOperationException($"Unsafe generated identifier '{value}'.");
        }
    }

    private static string Quote(string value)
    {
        EnsureIdentifier(value);
        return $"\"{value}\"";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly RuntimeFieldDefinition CreatedAtSystemField = new(
        "created_at",
        "_runtime",
        "created_at",
        "Created at",
        "datetime",
        "created_at",
        "timestamptz",
        false,
        false,
        null,
        [],
        null);
}
