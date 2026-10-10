using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeRecordStore
{
    private void EnsureEntityActionAllowed(
        RuntimePermissionSnapshot snapshot,
        string action,
        RuntimeEntityDefinition entity)
    {
        if (!permissions.IsAllowed(snapshot, action, entity))
        {
            throw new RuntimeRecordPermissionException(action, entity.Key);
        }
    }

    private async Task<RuntimeRecordAccessPredicate> BuildReadPredicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        string sqlAlias,
        string parameterPrefix,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        EnsureEntityActionAllowed(snapshot, "read", entity);
        var readPredicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction,
            snapshot,
            user,
            "read",
            entity,
            sqlAlias,
            $"{parameterPrefix}_read",
            cancellationToken);
        if (!includeArchived)
        {
            return readPredicate;
        }

        EnsureEntityActionAllowed(snapshot, "restore", entity);
        var restorePredicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction,
            snapshot,
            user,
            "restore",
            entity,
            sqlAlias,
            $"{parameterPrefix}_restore",
            cancellationToken);
        if (readPredicate.DeniesAllRows)
        {
            return RuntimeRecordAccessPredicate.DenyAll;
        }

        var qualifiedAlias = Quote(sqlAlias);
        return new RuntimeRecordAccessPredicate(
            $"(({readPredicate.Sql}) and ({qualifiedAlias}.\"archived_at\" is null or ({restorePredicate.Sql})))",
            readPredicate.Parameters.Concat(restorePredicate.Parameters).ToArray(),
            AllowsAllRows: readPredicate.AllowsAllRows && restorePredicate.AllowsAllRows,
            DeniesAllRows: false);
    }

    private async Task<bool> IsMutationOutputReadableAsync(
        NpgsqlConnection connection,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken) =>
        permissions.IsAllowed(snapshot, "read", entity) &&
        await permissions.IsCandidateRecordAllowedAsync(
            connection,
            transaction: null,
            snapshot,
            user,
            "read",
            entity,
            record,
            cancellationToken);

    private static Dictionary<string, object?> MinimalMutationOutput(
        IReadOnlyDictionary<string, object?> record) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = record.GetValueOrDefault("id")
    };

    private static bool IsTextLike(RuntimeFieldDefinition field) =>
        field.FieldType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
        field.FieldType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        field.FieldType.Equals("enum", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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

    private sealed record RuntimeWhere(string Sql);
}
