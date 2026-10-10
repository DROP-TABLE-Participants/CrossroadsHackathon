using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeRecordStore
{
    private async Task<NpgsqlConnection> OpenConnectionAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

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
        var structure = RuntimeEntityStructureMetadata.Read(metadata);
        var entity = new RuntimeEntityDefinition(
            tableName,
            id,
            name,
            displayName,
            "app",
            tableName,
            display.PrimaryField ?? string.Empty,
            display.SecondaryField,
            [])
        {
            RelationCreateGuards = RuntimeRelationCreateGuardMetadata.Read(metadata),
            Relationships = structure.Relationships,
            Collections = structure.Collections
        };
        await reader.DisposeAsync();

        EnsureIdentifier(entity.SchemaName);
        EnsureIdentifier(entity.TableName);

        return ApplyDisplayDefaults(entity with
        {
            Fields = await LoadFieldsAsync(connection, entity.Id, cancellationToken)
        });
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
            IReadOnlyList<string> options = metadata.TryGetProperty("Options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Array
                ? optionsElement.EnumerateArray()
                    .Where(option => option.ValueKind == JsonValueKind.String)
                    .Select(option => option.GetString()!)
                    .ToArray()
                : [];

            var computed = ReadComputed(metadata);
            var field = new RuntimeFieldDefinition(
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
                    RuntimeAuthoritativeFieldDefinitions.GetRequired(authoritativeFields, fieldId)));

            EnsureIdentifier(field.ColumnName);
            fields.Add(field);
        }

        return fields;
    }

    private static RuntimeEntityDefinition ApplyDisplayDefaults(RuntimeEntityDefinition entity)
    {
        var primary = string.IsNullOrWhiteSpace(entity.DisplayPrimaryField)
            ? InferDisplayField(entity.Fields)
            : entity.DisplayPrimaryField;
        var secondary = string.IsNullOrWhiteSpace(entity.DisplaySecondaryField)
            ? InferSecondaryDisplayField(entity.Fields, primary)
            : entity.DisplaySecondaryField;

        if (primary != "id" && !entity.Fields.Any(field => field.Name.Equals(primary, StringComparison.OrdinalIgnoreCase)))
        {
            primary = InferDisplayField(entity.Fields);
        }

        if (secondary is not null &&
            secondary != "id" &&
            !entity.Fields.Any(field => field.Name.Equals(secondary, StringComparison.OrdinalIgnoreCase)))
        {
            secondary = InferSecondaryDisplayField(entity.Fields, primary);
        }

        return entity with
        {
            DisplayPrimaryField = primary,
            DisplaySecondaryField = secondary
        };
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

    private static string InferDisplayField(IReadOnlyList<RuntimeFieldDefinition> fields)
    {
        var candidates = new[] { "name", "title", "email", "company_name", "full_name", "invoice_number" };
        foreach (var candidate in candidates)
        {
            var field = fields.FirstOrDefault(field => field.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return field.Name;
            }
        }

        return fields.FirstOrDefault(IsTextLike)?.Name ?? "id";
    }

    private static string? InferSecondaryDisplayField(IReadOnlyList<RuntimeFieldDefinition> fields, string primary)
    {
        var candidates = new[] { "email", "contact_email", "company_name", "customer_id", "role", "status" };
        foreach (var candidate in candidates)
        {
            var field = fields.FirstOrDefault(field =>
                !field.Name.Equals(primary, StringComparison.OrdinalIgnoreCase) &&
                field.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return field.Name;
            }
        }

        return fields.FirstOrDefault(field => !field.Name.Equals(primary, StringComparison.OrdinalIgnoreCase) && IsTextLike(field))?.Name;
    }

}
