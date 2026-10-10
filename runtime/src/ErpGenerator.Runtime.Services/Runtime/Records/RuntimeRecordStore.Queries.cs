using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeRecordStore
{
    private static RuntimeWhere BuildListWhere(
        RuntimeEntityDefinition entity,
        string sqlAlias,
        string? search,
        bool includeArchived,
        RuntimeRecordAccessPredicate predicate,
        out string? searchParameter)
    {
        searchParameter = null;
        var qualifiedAlias = Quote(sqlAlias);
        var clauses = new List<string> { predicate.Sql };
        if (!includeArchived)
        {
            clauses.Add($"{qualifiedAlias}.\"archived_at\" is null");
        }

        if (string.IsNullOrWhiteSpace(search))
        {
            return new RuntimeWhere(clauses.Count == 0 ? string.Empty : $"where {string.Join(" and ", clauses)}");
        }

        var textFields = entity.Fields
            .Where(field => field.Computed is null)
            .Where(field => field.FieldType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                            field.FieldType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                            field.FieldType.Equals("enum", StringComparison.OrdinalIgnoreCase))
            .Select(field => $"{qualifiedAlias}.{Quote(field.ColumnName)}::text ilike @q")
            .ToArray();

        if (textFields.Length > 0)
        {
            searchParameter = $"%{search.Trim()}%";
            clauses.Add($"({string.Join(" or ", textFields)})");
        }

        return new RuntimeWhere(clauses.Count == 0 ? string.Empty : $"where {string.Join(" and ", clauses)}");
    }

    private static string ResolveSortColumn(RuntimeEntityDefinition entity, string? sortBy)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return "id";
        }

        var normalized = sortBy.Trim();
        if (normalized is "id" or "created_at" or "updated_at")
        {
            return normalized;
        }

        var field = entity.Fields.FirstOrDefault(field =>
            string.Equals(field.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field.ColumnName, normalized, StringComparison.OrdinalIgnoreCase));

        if (field is null)
        {
            throw new RuntimeRecordValidationException([new RuntimeValidationError("sortBy", $"Unknown sort field '{sortBy}'.")]);
        }

        if (field.Computed is not null)
        {
            throw new RuntimeRecordValidationException([new RuntimeValidationError("sortBy", $"Computed field '{sortBy}' cannot be used for sorting in this pass.")]);
        }

        return field.ColumnName;
    }
    private static async Task<Dictionary<string, object?>?> ReadRecordByIdAsync(
        NpgsqlConnection connection,
        RuntimeEntityDefinition entity,
        long recordId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null,
        bool includeArchived = false,
        RuntimeRecordAccessPredicate? predicate = null,
        bool forUpdate = false)
    {
        predicate ??= RuntimeRecordAccessPredicate.AllowAll;
        await using var command = new NpgsqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = $"""
                select {BuildSelectColumns(entity)}
                from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as "r"
                where "r"."id" = @id
                  and ({predicate.Sql})
                {(includeArchived ? string.Empty : "and \"r\".\"archived_at\" is null")}
                {(forUpdate ? "for update of \"r\"" : string.Empty)}
                """
        };
        command.Parameters.AddWithValue("id", recordId);
        predicate.AddParameters(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    private static Dictionary<string, object?> ReadRecord(NpgsqlDataReader reader)
    {
        var record = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            record[reader.GetName(index)] = reader.IsDBNull(index) ? null : NormalizeValue(reader.GetValue(index));
        }

        return record;
    }

    private static object NormalizeValue(object value) => value switch
    {
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime,
        DateTimeOffset dateTimeOffset => dateTimeOffset,
        decimal number => number,
        _ => value
    };

    private static string BuildSelectColumns(RuntimeEntityDefinition entity)
    {
        var columns = new[] { "id", "created_at", "created_by", "updated_at", "updated_by", "archived_at", "archived_by", "archive_reason" }
            .Concat(entity.Fields.Where(field => field.Computed is null).Select(field => field.ColumnName))
            .Select(Quote);

        return string.Join(", ", columns);
    }

}
