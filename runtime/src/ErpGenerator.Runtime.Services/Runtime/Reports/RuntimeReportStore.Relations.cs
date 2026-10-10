using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeReportStore
{
    private async Task HydrateRelationGroupLabelsAsync(
        NpgsqlConnection connection,
        RuntimeReportDefinition report,
        RuntimeEntityDefinition entity,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var fieldsByKey = BuildFieldLookup(entity.Fields);
        foreach (var group in report.GroupBy)
        {
            var field = ResolveField(fieldsByKey, group.Field);
            if (field is null ||
                !field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(field.ReferencesEntity))
            {
                continue;
            }

            var ids = rows
                .Select(row => ToLong(row.GetValueOrDefault(group.Field)))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray();
            var labels = await LookupLabelsAsync(connection, field.ReferencesEntity!, ids, cancellationToken);
            foreach (var row in rows)
            {
                var id = ToLong(row.GetValueOrDefault(group.Field));
                if (id.HasValue && labels.TryGetValue(id.Value, out var label))
                {
                    row[$"{group.Field}__label"] = label;
                }
            }
        }
    }

    private static async Task<IReadOnlyDictionary<long, string>> LookupLabelsAsync(
        NpgsqlConnection connection,
        string entityKey,
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        var entity = await LoadEntityByIdAsync(connection, entityKey, cancellationToken);
        if (entity is null)
        {
            return new Dictionary<long, string>();
        }

        var labelField = entity.Fields.FirstOrDefault(field =>
            field.Computed is null &&
            (field.Name.Equals(entity.DisplayPrimaryField, StringComparison.OrdinalIgnoreCase) ||
             field.ColumnName.Equals(entity.DisplayPrimaryField, StringComparison.OrdinalIgnoreCase)));
        var labelExpression = labelField is null ? "\"id\"::text" : $"{Quote(labelField.ColumnName)}::text";
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select id, {labelExpression}
            from {Quote(entity.SchemaName)}.{Quote(entity.TableName)}
            where id = any(@ids)
            """;
        command.Parameters.AddWithValue("ids", ids.ToArray());

        var labels = new Dictionary<long, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            labels[id] = reader.IsDBNull(1) ? $"#{id}" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? $"#{id}";
        }

        return labels;
    }
}
