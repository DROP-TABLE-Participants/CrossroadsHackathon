using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeReportStore
{
    private static async Task<RuntimeEntityDefinition?> LoadEntityAsync(
        NpgsqlConnection connection,
        string entityKey,
        CancellationToken cancellationToken) =>
        await LoadEntityMatchAsync(connection, entityKey, exactIdOnly: false, cancellationToken);

    private static async Task<RuntimeEntityDefinition?> LoadEntityByIdAsync(
        NpgsqlConnection connection,
        string entityId,
        CancellationToken cancellationToken) =>
        await LoadEntityMatchAsync(connection, entityId, exactIdOnly: true, cancellationToken);

    private static async Task<RuntimeEntityDefinition?> LoadEntityMatchAsync(
        NpgsqlConnection connection,
        string entityKey,
        bool exactIdOnly,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entityKey))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        var normalizedKey = entityKey.Trim();
        command.CommandText = exactIdOnly
            ? """
              select id, name, display_name, table_name, metadata
              from _runtime.entity_definitions
              where id = @key
              limit 1
              """
            : """
              select id, name, display_name, table_name, metadata
              from _runtime.entity_definitions
              where id = @key or name = @key or table_name = @key
              order by case when id = @key then 0 when name = @key then 1 else 2 end, id
              limit 2
              """;
        command.Parameters.AddWithValue("key", normalizedKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var id = reader.GetString(0);
        var name = reader.GetString(1);
        var displayName = reader.GetString(2);
        var tableName = reader.GetString(3);
        var metadataJson = reader.GetString(4);
        if (!exactIdOnly &&
            !id.Equals(normalizedKey, StringComparison.Ordinal) &&
            await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var metadata = JsonDocument.Parse(metadataJson).RootElement;
        var display = ReadDisplay(metadata);
        var entity = new RuntimeEntityDefinition(
            tableName,
            id,
            name,
            displayName,
            "app",
            tableName,
            display.PrimaryField ?? string.Empty,
            display.SecondaryField,
            []);
        await reader.DisposeAsync();

        return entity with
        {
            Fields = await LoadFieldsAsync(connection, entity.Id, cancellationToken)
        };
    }

    private static async Task<IReadOnlyList<RuntimeFieldDefinition>> LoadFieldsAsync(
        NpgsqlConnection connection,
        string entityId,
        CancellationToken cancellationToken)
    {
        var fields = new List<RuntimeFieldDefinition>();
        var authoritativeFields = await RuntimeAuthoritativeFieldDefinitions.LoadAsync(
            connection,
            entityId,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, entity_id, name, display_name, field_type, column_name, sql_type, is_required, is_unique, references_entity, metadata
            from _runtime.field_definitions
            where entity_id = @entityId
            order by name
            """;
        command.Parameters.AddWithValue("entityId", entityId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fieldId = reader.GetString(0);
            var metadata = JsonDocument.Parse(reader.GetString(10)).RootElement;
            var options = metadata.TryGetProperty("Options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Array
                ? optionsElement.EnumerateArray()
                    .Where(option => option.ValueKind == JsonValueKind.String)
                    .Select(option => option.GetString()!)
                    .ToArray()
                : [];
            var computed = ReadComputed(metadata);
            fields.Add(new RuntimeFieldDefinition(
                fieldId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                options,
                computed,
                RuntimeFieldLifecycleMetadata.Read(
                    metadata,
                    reader.GetString(4),
                    reader.GetBoolean(7),
                    reader.GetBoolean(8),
                    options,
                    computed is not null,
                    RuntimeAuthoritativeFieldDefinitions.GetRequired(authoritativeFields, fieldId))));
        }

        return fields;
    }

    private static RuntimeReportDefinition ReadReport(string metadata)
    {
        var compiled = JsonSerializer.Deserialize<ReportDefinition>(metadata, JsonOptions)
                       ?? throw new InvalidOperationException("Report metadata could not be read.");
        return new RuntimeReportDefinition(
            compiled.Key,
            compiled.Label,
            compiled.Description,
            compiled.SourceEntity,
            compiled.GroupBy.Select(group => new RuntimeReportGroupBy(group.Field, group.Label)
            {
                IdentityEntityId = group.IdentityEntityId,
                EvidenceValueKind = group.EvidenceValueKind ?? string.Empty
            }).ToArray(),
            compiled.Metrics.Select(metric => new RuntimeReportMetric(
                metric.Key,
                metric.Label,
                metric.Op,
                metric.Field,
                metric.Filter is null ? null : ToRuntimeFilter(metric.Filter))
            {
                EvidenceValueKind = metric.EvidenceValueKind ?? string.Empty
            }).ToArray(),
            compiled.Filters.Select(ToRuntimeFilter).ToArray(),
            (compiled.Joins ?? [])
                .Select(join => new RuntimeReportJoin(
                    join.Alias,
                    join.FromAlias,
                    join.RelationField,
                    join.TargetEntity,
                    join.Type,
                    join.Depth))
                .ToArray());
    }

    private static RuntimeReportFilter ToRuntimeFilter(ReportFilterDefinition filter) =>
        new(
            filter.Field,
            filter.Op,
            JsonElementToObject(filter.Value),
            filter.Values.Select(JsonElementToObject).ToArray());

    private static RuntimeComputedField? ReadComputed(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("Computed", out var computed) &&
            !metadata.TryGetProperty("computed", out computed))
        {
            return null;
        }

        if (computed.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var op = ReadString(computed, "Op") ?? ReadString(computed, "op");
        if (string.IsNullOrWhiteSpace(op))
        {
            return null;
        }

        return new RuntimeComputedField(
            op.Trim().ToLowerInvariant(),
            ReadComputedOperand(computed, "Left") ?? ReadComputedOperand(computed, "left") ?? new RuntimeComputedOperand(null, null),
            ReadComputedOperand(computed, "Right") ?? ReadComputedOperand(computed, "right") ?? new RuntimeComputedOperand(null, null));
    }

    private static RuntimeComputedOperand? ReadComputedOperand(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var operand) || operand.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var field = ReadString(operand, "Field") ?? ReadString(operand, "field");
        object? value = null;
        if (operand.TryGetProperty("Value", out var valueElement) || operand.TryGetProperty("value", out valueElement))
        {
            value = JsonElementToObject(valueElement);
        }

        return new RuntimeComputedOperand(string.IsNullOrWhiteSpace(field) ? null : field.Trim(), value);
    }

    private static RuntimeEntityDisplay ReadDisplay(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("Display", out var display) || display.ValueKind != JsonValueKind.Object)
        {
            return new RuntimeEntityDisplay(null, null);
        }

        var primary = ReadString(display, "PrimaryField") ?? ReadString(display, "primaryField");
        var secondary = ReadString(display, "SecondaryField") ?? ReadString(display, "secondaryField");
        return new RuntimeEntityDisplay(primary, secondary);
    }

    private static IReadOnlyDictionary<string, RuntimeFieldDefinition> BuildFieldLookup(IReadOnlyList<RuntimeFieldDefinition> fields)
    {
        var lookup = new Dictionary<string, RuntimeFieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            lookup[field.Name] = field;
            lookup[field.Id.Split('.').Last()] = field;
            if (field.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase))
            {
                lookup[field.Name[..^3]] = field;
            }
        }

        return lookup;
    }

    private static RuntimeFieldDefinition? ResolveField(
        IReadOnlyDictionary<string, RuntimeFieldDefinition> fieldsByKey,
        string fieldKey) =>
        fieldsByKey.TryGetValue(fieldKey, out var field) ? field : null;
}
