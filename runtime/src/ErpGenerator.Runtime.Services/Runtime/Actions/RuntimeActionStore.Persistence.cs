using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeActionStore
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
            RelationCreateGuards = RuntimeRelationCreateGuardMetadata.Read(metadata)
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

    private static async Task<ActionDefinition?> LoadActionAsync(
        NpgsqlConnection connection,
        string entityId,
        string actionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select id, action_type, metadata
                from _runtime.action_definitions
                where entity_id = @entityId and id = @actionKey
                limit 1
                """;
            command.Parameters.AddWithValue("entityId", entityId);
            command.Parameters.AddWithValue("actionKey", actionKey.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return TryReadAction(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<ActionDefinition>> LoadActionsForEntityAsync(
        NpgsqlConnection connection,
        string entityId,
        CancellationToken cancellationToken)
    {
        try
        {
            var actions = new List<ActionDefinition>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select id, action_type, metadata
                from _runtime.action_definitions
                where entity_id = @entityId
                order by label
                """;
            command.Parameters.AddWithValue("entityId", entityId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var action = TryReadAction(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (action is not null)
                {
                    actions.Add(action);
                }
            }

            return actions;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }

    private static ActionDefinition? TryReadAction(string storedKey, string storedType, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var action = JsonSerializer.Deserialize<ActionDefinition>(json, JsonOptions);
            if (action is null ||
                string.IsNullOrWhiteSpace(action.Key) ||
                string.IsNullOrWhiteSpace(action.Type) ||
                !action.Key.Trim().Equals(storedKey.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !action.Type.Trim().Equals(storedType.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return action;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Corrupt persisted metadata is unavailable to callers. A future
            // observability hook can surface this without weakening the deny.
            return null;
        }
    }

    private static async Task<Dictionary<string, object?>?> ReadRecordByIdAsync(
        NpgsqlConnection connection,
        RuntimeEntityDefinition entity,
        long recordId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null,
        bool forUpdate = false)
    {
        await using var command = new NpgsqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = $"""
                select {BuildSelectColumns(entity)}
                from {Quote(entity.SchemaName)}.{Quote(entity.TableName)}
                where id = @id and archived_at is null
                {(forUpdate ? "for update" : string.Empty)}
                """
        };
        command.Parameters.AddWithValue("id", recordId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ApplyComputedFields(entity, ReadRecord(reader)) : null;
    }

    private async Task<bool> RelationExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeUserContext user,
        string referencesEntity,
        long recordId,
        CancellationToken cancellationToken,
        bool acquireShareLock = true,
        string? requiredReadField = null)
    {
        var entity = await LoadEntityByIdAsync(connection, referencesEntity, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        if (!permissions.IsAllowed(permissionSnapshot, "read", entity))
        {
            throw RecordScopePermissionDenied("read", entity);
        }

        if (!string.IsNullOrWhiteSpace(requiredReadField) &&
            !permissions.IsFieldAllowed(permissionSnapshot, "read", entity, requiredReadField))
        {
            throw new RuntimeActionPermissionException(
                "field.read",
                $"{entity.Id}.{requiredReadField}",
                "Action record access denied.");
        }

        const string recordAlias = "action_relation";
        var predicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            "read",
            entity,
            recordAlias,
            "action_relation_scope",
            cancellationToken);
        var lockClause = acquireShareLock ? $"for share of {Quote(recordAlias)}" : string.Empty;
        await using var command = new NpgsqlCommand(
            $"""
            select {BuildSelectColumns(entity)}
            from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as {Quote(recordAlias)}
            where {Quote(recordAlias)}."id" = @id
              and {Quote(recordAlias)}."archived_at" is null
              and ({predicate.Sql})
            {lockClause}
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", recordId);
        predicate.AddParameters(command);
        Dictionary<string, object?>? targetRecord;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            targetRecord = await reader.ReadAsync(cancellationToken)
                ? ReadRecord(reader)
                : null;
        }

        return targetRecord is not null &&
               await permissions.IsCandidateRecordAllowedAsync(
                   connection,
                   transaction,
                   permissionSnapshot,
                   user,
                   "read",
                   entity,
                   targetRecord,
                   cancellationToken);
    }

    private async Task EnsureGuardedRelationAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeUserContext user,
        IReadOnlyList<RuntimeGuardedRelationLock> guardedRelations,
        CancellationToken cancellationToken)
    {
        foreach (var guarded in guardedRelations)
        {
            if (await RelationExistsAsync(
                    connection,
                    transaction,
                    permissionSnapshot,
                    user,
                    guarded.ReferencedEntity,
                    guarded.ReferencedRecordId,
                    cancellationToken,
                    acquireShareLock: false,
                    requiredReadField: guarded.ReferencedField))
            {
                continue;
            }

            throw new RuntimeActionValidationException(
                $"{guarded.Relation.DisplayName} references a missing record.");
        }
    }

    private static RuntimeEntityDefinition ApplyDisplayDefaults(RuntimeEntityDefinition entity)
    {
        var primary = string.IsNullOrWhiteSpace(entity.DisplayPrimaryField)
            ? InferDisplayField(entity.Fields)
            : entity.DisplayPrimaryField;
        var secondary = string.IsNullOrWhiteSpace(entity.DisplaySecondaryField)
            ? InferSecondaryDisplayField(entity.Fields, primary)
            : entity.DisplaySecondaryField;
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

    private static Dictionary<string, object?> ApplyComputedFields(RuntimeEntityDefinition entity, Dictionary<string, object?> record)
    {
        var fieldsByKey = entity.Fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var field in entity.Fields.Where(field => field.Computed is not null))
        {
            record[field.ColumnName] = EvaluateComputedField(field, fieldsByKey, record);
        }

        return record;
    }

    private static object? EvaluateComputedField(
        RuntimeFieldDefinition field,
        IReadOnlyDictionary<string, RuntimeFieldDefinition> fieldsByKey,
        IReadOnlyDictionary<string, object?> record)
    {
        var computed = field.Computed;
        if (computed is null)
        {
            return null;
        }

        var left = ResolveComputedOperand(computed.Left, fieldsByKey, record);
        var right = ResolveComputedOperand(computed.Right, fieldsByKey, record);
        return computed.Op switch
        {
            "add" => ComputeNumeric(left, right, (leftNumber, rightNumber) => leftNumber + rightNumber),
            "subtract" => ComputeNumeric(left, right, (leftNumber, rightNumber) => leftNumber - rightNumber),
            "multiply" => ComputeNumeric(left, right, (leftNumber, rightNumber) => leftNumber * rightNumber),
            "divide" => TryDecimal(right, out var rightNumber) && rightNumber != 0
                ? ComputeNumeric(left, right, (leftNumber, denominator) => leftNumber / denominator)
                : null,
            "concat" => $"{left}{right}",
            "coalesce" => IsNullLike(left) ? right : left,
            _ => null
        };
    }

    private static object? ResolveComputedOperand(
        RuntimeComputedOperand operand,
        IReadOnlyDictionary<string, RuntimeFieldDefinition> fieldsByKey,
        IReadOnlyDictionary<string, object?> record)
    {
        if (!string.IsNullOrWhiteSpace(operand.Field) && fieldsByKey.TryGetValue(operand.Field, out var field))
        {
            return record.GetValueOrDefault(field.ColumnName);
        }

        return operand.Value;
    }

    private static decimal? ComputeNumeric(object? left, object? right, Func<decimal, decimal, decimal> operation) =>
        TryDecimal(left, out var leftNumber) && TryDecimal(right, out var rightNumber)
            ? operation(leftNumber, rightNumber)
            : null;

    private static Dictionary<string, object?> ReadRecord(NpgsqlDataReader reader) =>
        ReadRecord(reader, reader.FieldCount);

    private static Dictionary<string, object?> ReadRecord(NpgsqlDataReader reader, int fieldCount)
    {
        var record = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fieldCount; index++)
        {
            record[reader.GetName(index)] = reader.IsDBNull(index) ? null : NormalizeValue(reader.GetValue(index));
        }

        return record;
    }

    private static object NormalizeValue(object value) => value switch
    {
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal number => number,
        _ => value
    };

    private static string BuildSelectColumns(RuntimeEntityDefinition entity, string? alias = null)
    {
        var prefix = alias is null ? string.Empty : $"{Quote(alias)}.";
        var columns = new[] { "id", "created_at", "created_by", "updated_at", "updated_by", "archived_at", "archived_by", "archive_reason" }
            .Concat(entity.Fields.Where(field => field.Computed is null).Select(field => field.ColumnName))
            .Select(column => $"{prefix}{Quote(column)}");

        return string.Join(", ", columns);
    }

    private static string InferDisplayField(IReadOnlyList<RuntimeFieldDefinition> fields) =>
        fields.FirstOrDefault(field => field.Name.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                                       field.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                                       field.Name.Equals("company_name", StringComparison.OrdinalIgnoreCase) ||
                                       field.Name.Equals("invoice_number", StringComparison.OrdinalIgnoreCase))?.Name ??
        fields.FirstOrDefault(field => IsTextLike(field))?.Name ??
        "id";

    private static string? InferSecondaryDisplayField(IReadOnlyList<RuntimeFieldDefinition> fields, string primary) =>
        fields.FirstOrDefault(field => !field.Name.Equals(primary, StringComparison.OrdinalIgnoreCase) && IsTextLike(field))?.Name;

    private static bool IsTextLike(RuntimeFieldDefinition field) =>
        field.FieldType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
        field.FieldType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        field.FieldType.Equals("enum", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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
}
