using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeWorkflowStore
{
    private async Task<NpgsqlConnection> OpenConnectionAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<IReadOnlyList<RuntimeWorkflowDefinition>> LoadWorkflowsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var workflows = new List<RuntimeWorkflowDefinition>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select metadata
            from _runtime.workflow_definitions
            order by name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            workflows.Add(ReadWorkflow(JsonDocument.Parse(reader.GetString(0)).RootElement));
        }

        return workflows;
    }

    private static async Task<IReadOnlyList<RuntimeWorkflowDefinition>> LoadWorkflowsForEntityAsync(
        NpgsqlConnection connection,
        string entityId,
        CancellationToken cancellationToken)
    {
        var workflows = new List<RuntimeWorkflowDefinition>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select metadata
            from _runtime.workflow_definitions
            where entity_id = @entityId
            order by name
            """;
        command.Parameters.AddWithValue("entityId", entityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            workflows.Add(ReadWorkflow(JsonDocument.Parse(reader.GetString(0)).RootElement));
        }

        return workflows;
    }

    private static async Task<RuntimeWorkflowDefinition?> LoadWorkflowAsync(
        NpgsqlConnection connection,
        string workflowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select metadata
            from _runtime.workflow_definitions
            where id = @workflowId
            limit 1
            """;
        command.Parameters.AddWithValue("workflowId", workflowId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : ReadWorkflow(JsonDocument.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!).RootElement);
    }

    private static RuntimeWorkflowDefinition ReadWorkflow(JsonElement element)
    {
        var hasTrigger = (element.TryGetProperty("Trigger", out var triggerElement) ||
                          element.TryGetProperty("trigger", out triggerElement)) &&
                         triggerElement.ValueKind == JsonValueKind.Object;
        var trigger = hasTrigger
            ? new RuntimeWorkflowTrigger(
                ReadString(triggerElement, "Action") ??
                ReadString(triggerElement, "action") ??
                string.Empty)
            : null;

        if (trigger is not null && string.IsNullOrWhiteSpace(trigger.Action))
        {
            trigger = null;
        }

        return new RuntimeWorkflowDefinition(
            ReadString(element, "Id") ?? ReadString(element, "id") ?? string.Empty,
            ReadString(element, "Name") ?? ReadString(element, "name") ?? string.Empty,
            ReadString(element, "Entity") ?? ReadString(element, "entity") ?? string.Empty,
            trigger,
            ReadSteps(element, "Steps", "steps"),
            ReadSteps(element, "OnRejected", "onRejected"))
        {
            StartGuards = ReadStartGuards(element)
        };
    }

    private static IReadOnlyList<RuntimeWorkflowStartGuard> ReadStartGuards(JsonElement element)
    {
        if ((!element.TryGetProperty("StartGuards", out var guardsElement) &&
             !element.TryGetProperty("startGuards", out guardsElement) &&
             !element.TryGetProperty("start_guards", out guardsElement)) ||
            guardsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return guardsElement.EnumerateArray().Select(guard =>
        {
            var filters = new List<ActionFilterDefinition>();
            if ((guard.TryGetProperty("Where", out var whereElement) ||
                 guard.TryGetProperty("where", out whereElement)) &&
                whereElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var filter in whereElement.EnumerateArray())
                {
                    object? value = null;
                    if (filter.TryGetProperty("Value", out var valueElement) ||
                        filter.TryGetProperty("value", out valueElement))
                    {
                        value = valueElement.Clone();
                    }

                    ActionValueFromDefinition? valueFrom = null;
                    if ((filter.TryGetProperty("ValueFrom", out var valueFromElement) ||
                         filter.TryGetProperty("valueFrom", out valueFromElement) ||
                         filter.TryGetProperty("value_from", out valueFromElement)) &&
                        valueFromElement.ValueKind == JsonValueKind.Object)
                    {
                        valueFrom = new ActionValueFromDefinition(
                            ReadString(valueFromElement, "Record") ?? ReadString(valueFromElement, "record"),
                            ReadString(valueFromElement, "Item") ?? ReadString(valueFromElement, "item"),
                            ReadString(valueFromElement, "Step") ?? ReadString(valueFromElement, "step"),
                            ReadString(valueFromElement, "Field") ?? ReadString(valueFromElement, "field"),
                            ReadString(valueFromElement, "Now") ?? ReadString(valueFromElement, "now"));
                    }

                    var values = new List<object?>();
                    if ((filter.TryGetProperty("Values", out var valuesElement) ||
                         filter.TryGetProperty("values", out valuesElement)) &&
                        valuesElement.ValueKind == JsonValueKind.Array)
                    {
                        values.AddRange(valuesElement.EnumerateArray().Select(item => (object?)item.Clone()));
                    }

                    filters.Add(new ActionFilterDefinition(
                        ReadString(filter, "Field") ?? ReadString(filter, "field") ?? string.Empty,
                        ReadString(filter, "Op") ?? ReadString(filter, "op") ?? string.Empty,
                        value,
                        valueFrom,
                        values));
                }
            }

            return new RuntimeWorkflowStartGuard(
                ReadString(guard, "Id") ?? ReadString(guard, "id") ?? string.Empty,
                ReadString(guard, "Entity") ?? ReadString(guard, "entity") ?? string.Empty,
                filters,
                ReadString(guard, "ResultCountOp") ??
                ReadString(guard, "resultCountOp") ??
                ReadString(guard, "result_count_op") ?? string.Empty,
                ReadInt32(guard, "ResultCount", "resultCount", "result_count"));
        }).ToArray();
    }

    private static int ReadInt32(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var result))
            {
                return result;
            }
        }

        return -1;
    }

    private static IReadOnlyList<RuntimeWorkflowStep> ReadSteps(JsonElement element, string pascalName, string camelName)
    {
        if ((!element.TryGetProperty(pascalName, out var stepsElement) && !element.TryGetProperty(camelName, out stepsElement)) ||
            stepsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return stepsElement.EnumerateArray().Select(step =>
        {
            RuntimeWorkflowAssignment? assignedTo = null;
            if ((step.TryGetProperty("AssignedTo", out var assignedElement) || step.TryGetProperty("assignedTo", out assignedElement)) &&
                assignedElement.ValueKind == JsonValueKind.Object)
            {
                var role = ReadString(assignedElement, "Role") ?? ReadString(assignedElement, "role");
                var type = ReadString(assignedElement, "Type") ?? ReadString(assignedElement, "type");
                var path = ReadString(assignedElement, "Path") ?? ReadString(assignedElement, "path");
                var requiredRole = ReadString(assignedElement, "RequiredRole") ??
                                   ReadString(assignedElement, "requiredRole") ??
                                   ReadString(assignedElement, "required_role");
                var fallbackRole = ReadString(assignedElement, "FallbackRole") ??
                                   ReadString(assignedElement, "fallbackRole") ??
                                   ReadString(assignedElement, "fallback_role");
                if (!string.IsNullOrWhiteSpace(role) ||
                    !string.IsNullOrWhiteSpace(type) ||
                    !string.IsNullOrWhiteSpace(path) ||
                    !string.IsNullOrWhiteSpace(requiredRole) ||
                    !string.IsNullOrWhiteSpace(fallbackRole))
                {
                    assignedTo = new RuntimeWorkflowAssignment(
                        string.IsNullOrWhiteSpace(type) ? null : type,
                        string.IsNullOrWhiteSpace(role) ? null : role,
                        string.IsNullOrWhiteSpace(path) ? null : path,
                        string.IsNullOrWhiteSpace(requiredRole) ? null : requiredRole,
                        string.IsNullOrWhiteSpace(fallbackRole) ? null : fallbackRole);
                }
            }

            var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if ((step.TryGetProperty("Set", out var setElement) || step.TryGetProperty("set", out setElement)) &&
                setElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in setElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        set[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }

            return new RuntimeWorkflowStep(
                ReadString(step, "Id") ?? ReadString(step, "id") ?? string.Empty,
                (ReadString(step, "Type") ?? ReadString(step, "type") ?? string.Empty).ToLowerInvariant(),
                ReadString(step, "Label") ?? ReadString(step, "label") ?? string.Empty,
                assignedTo,
                ReadString(step, "ApproveLabel") ?? ReadString(step, "approveLabel"),
                ReadString(step, "RejectLabel") ?? ReadString(step, "rejectLabel"),
                set,
                ReadCondition(step, "Condition", "condition") ?? ReadCondition(step, "If", "if"),
                ReadSteps(step, "Then", "then"),
                ReadString(step, "Event") ?? ReadString(step, "event"),
                ReadString(step, "Message") ?? ReadString(step, "message"),
                ReadStringArray(step, "ReviewFields", "reviewFields"));
        }).ToArray();
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string pascalName,
        string camelName)
    {
        if ((!element.TryGetProperty(pascalName, out var values) &&
             !element.TryGetProperty(camelName, out values)) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
    }

    private static RuntimeWorkflowCondition? ReadCondition(JsonElement element, string pascalName, string camelName)
    {
        if ((!element.TryGetProperty(pascalName, out var conditionElement) && !element.TryGetProperty(camelName, out conditionElement)) ||
            conditionElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var conditions = new List<RuntimeWorkflowCondition>();
        if ((conditionElement.TryGetProperty("Conditions", out var conditionsElement) || conditionElement.TryGetProperty("conditions", out conditionsElement)) &&
            conditionsElement.ValueKind == JsonValueKind.Array)
        {
            conditions.AddRange(conditionsElement.EnumerateArray()
                .Select(item => ReadConditionFromElement(item))
                .OfType<RuntimeWorkflowCondition>());
        }

        return new RuntimeWorkflowCondition(
            ReadString(conditionElement, "Op") ?? ReadString(conditionElement, "op") ?? string.Empty,
            ReadOperand(conditionElement, "Left", "left"),
            ReadOperand(conditionElement, "Right", "right"),
            conditions);
    }

    private static RuntimeWorkflowCondition? ReadConditionFromElement(JsonElement conditionElement)
    {
        if (conditionElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var conditions = new List<RuntimeWorkflowCondition>();
        if ((conditionElement.TryGetProperty("Conditions", out var conditionsElement) || conditionElement.TryGetProperty("conditions", out conditionsElement)) &&
            conditionsElement.ValueKind == JsonValueKind.Array)
        {
            conditions.AddRange(conditionsElement.EnumerateArray()
                .Select(ReadConditionFromElement)
                .OfType<RuntimeWorkflowCondition>());
        }

        return new RuntimeWorkflowCondition(
            ReadString(conditionElement, "Op") ?? ReadString(conditionElement, "op") ?? string.Empty,
            ReadOperand(conditionElement, "Left", "left"),
            ReadOperand(conditionElement, "Right", "right"),
            conditions);
    }

    private static RuntimeWorkflowConditionOperand? ReadOperand(JsonElement conditionElement, string pascalName, string camelName)
    {
        if ((!conditionElement.TryGetProperty(pascalName, out var operandElement) && !conditionElement.TryGetProperty(camelName, out operandElement)) ||
            operandElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        object? value = null;
        if (operandElement.TryGetProperty("Value", out var valueElement) || operandElement.TryGetProperty("value", out valueElement))
        {
            value = valueElement.Clone();
        }

        return new RuntimeWorkflowConditionOperand(
            ReadString(operandElement, "Field") ?? ReadString(operandElement, "field"),
            ReadString(operandElement, "Path") ?? ReadString(operandElement, "path"),
            value);
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
            []);
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
            value = valueElement.ValueKind switch
            {
                JsonValueKind.String => valueElement.GetString(),
                JsonValueKind.Number => valueElement.TryGetInt64(out var integer) ? integer : valueElement.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
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

        return fields.FirstOrDefault(field => field.FieldType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                                             field.FieldType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                                             field.FieldType.Equals("enum", StringComparison.OrdinalIgnoreCase))?.Name ?? "id";
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

        return fields.FirstOrDefault(field =>
            !field.Name.Equals(primary, StringComparison.OrdinalIgnoreCase) &&
            (field.FieldType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
             field.FieldType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
             field.FieldType.Equals("enum", StringComparison.OrdinalIgnoreCase)))?.Name;
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
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    private static async Task ApplyRecordUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeEntityDefinition entity,
        long recordId,
        RuntimeUserContext? user,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return;
        }

        var converted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldName, value) in values)
        {
            var field = entity.Fields.FirstOrDefault(candidate =>
                candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
                candidate.ColumnName.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                throw new RuntimeWorkflowValidationException($"Workflow tried to set unknown field '{fieldName}'.");
            }

            if (field.Lifecycle?.RequiresNamedAction == true)
            {
                throw new RuntimeWorkflowValidationException(
                    $"Workflow cannot transition named-action lifecycle field '{entity.Key}.{field.Name}'.");
            }

            converted[field.ColumnName] = ConvertStepValue(field, value);
        }

        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        var columns = converted.Keys.ToArray();
        var assignments = columns.Select((column, index) => $"{Quote(column)} = @p{index}").ToList();
        assignments.Add("\"updated_at\" = now()");
        assignments.Add("\"updated_by\" = @updatedBy");
        command.CommandText = $"""
            update {Quote(entity.SchemaName)}.{Quote(entity.TableName)}
            set {string.Join(", ", assignments)}
            where id = @id and archived_at is null
            """;
        for (var index = 0; index < columns.Length; index++)
        {
            command.Parameters.AddWithValue($"p{index}", converted[columns[index]] ?? DBNull.Value);
        }

        command.Parameters.AddWithValue("id", recordId);
        command.Parameters.Add("updatedBy", NpgsqlDbType.Bigint).Value =
            (object?)user?.UserId ?? DBNull.Value;
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated == 0)
        {
            throw new RuntimeWorkflowRecordNotFoundException(entity.Key, recordId);
        }
    }

    private static object? ConvertStepValue(RuntimeFieldDefinition field, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return field.FieldType.ToLowerInvariant() switch
        {
            "string" or "text" => value.Trim(),
            "enum" => field.Options.FirstOrDefault(option => option.Equals(value, StringComparison.OrdinalIgnoreCase))
                      ?? throw new RuntimeWorkflowValidationException($"{field.Name} cannot be set to '{value}'."),
            "integer" => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            "number" or "decimal" or "money" => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),
            "boolean" => bool.Parse(value),
            "date" => DateOnly.Parse(value, CultureInfo.InvariantCulture),
            "datetime" => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture),
            "relation" => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            _ => throw new RuntimeWorkflowValidationException($"{field.Name} uses unsupported workflow update type '{field.FieldType}'.")
        };
    }
}
