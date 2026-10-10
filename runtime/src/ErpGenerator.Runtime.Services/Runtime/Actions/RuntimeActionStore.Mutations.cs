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
    private async Task<Dictionary<string, object?>> InsertRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeEntityDefinition entity,
        Dictionary<string, object?> values,
        string mutationAction,
        CancellationToken cancellationToken)
    {
        EnsureActionWritableValuesAllowed(permissionSnapshot, entity, values.Keys, mutationAction);
        foreach (var field in entity.Fields.Where(field => field.Lifecycle is not null))
        {
            if (values.ContainsKey(field.ColumnName))
            {
                throw new RuntimeActionValidationException(
                    $"Action cannot supply server-initialized lifecycle field '{entity.Key}.{field.Name}' while creating a record.");
            }

            values[field.ColumnName] = field.Lifecycle!.Initial;
        }

        values["created_by"] = user.UserId;
        values["updated_by"] = user.UserId;
        await EnsureEffectRecordAllowedAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            mutationAction,
            entity,
            values,
            cancellationToken);
        await RuntimeRelationCreateGuardEnforcer.EnsureAllowedAsync(
            connection,
            transaction,
            entity,
            values,
            (guardedRelations, token) => EnsureGuardedRelationAccessAsync(
                connection,
                transaction,
                permissionSnapshot,
                user,
                guardedRelations,
                token),
            cancellationToken);
        var columns = values.Keys.ToArray();
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        var parameterNames = columns.Select((_, index) => $"@p{index}").ToArray();
        command.CommandText = $"""
            insert into {Quote(entity.SchemaName)}.{Quote(entity.TableName)} ({string.Join(", ", columns.Select(Quote))})
            values ({string.Join(", ", parameterNames)})
            returning {BuildSelectColumns(entity)}
            """;
        for (var index = 0; index < columns.Length; index++)
        {
            command.Parameters.AddWithValue($"p{index}", values[columns[index]] ?? DBNull.Value);
        }

        Dictionary<string, object?> record;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Action insert did not return a row.");
            }

            record = ReadRecord(reader);
        }

        await EnsureEffectRecordAllowedAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            mutationAction,
            entity,
            record,
            cancellationToken);

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user,
            "record.created",
            $"Created {entity.DisplayName} record.",
            new Dictionary<string, object?>
            {
                ["entity"] = entity.Key,
                ["recordId"] = record["id"],
                ["fields"] = columns
            },
            cancellationToken);

        return ApplyComputedFields(entity, record);
    }

    private async Task<Dictionary<string, object?>?> UpdateRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeEntityDefinition entity,
        long recordId,
        Dictionary<string, object?> values,
        string mutationAction,
        CancellationToken cancellationToken)
    {
        foreach (var fieldKey in values.Keys)
        {
            var field = ResolveField(entity, fieldKey);
            if (field?.Lifecycle?.RequiresNamedAction == true &&
                !mutationAction.Equals(ActionPermissionEffectAnalyzer.EffectUpdate, StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeActionValidationException(
                    $"Named-action lifecycle field '{entity.Key}.{field.Name}' requires effect_update authority.");
            }
        }

        EnsureActionWritableValuesAllowed(permissionSnapshot, entity, values.Keys, mutationAction);
        if (values.Count == 0)
        {
            var unchanged = await ReadRecordByIdAsync(
                connection,
                entity,
                recordId,
                cancellationToken,
                transaction,
                forUpdate: true);
            if (unchanged is not null)
            {
                await EnsureEffectRecordAllowedAsync(
                    connection,
                    transaction,
                    permissionSnapshot,
                    user,
                    mutationAction,
                    entity,
                    unchanged,
                    cancellationToken);
            }

            return unchanged;
        }

        var before = await ReadRecordByIdAsync(connection, entity, recordId, cancellationToken, transaction, forUpdate: true);
        if (before is null)
        {
            return null;
        }

        await EnsureEffectRecordAllowedAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            mutationAction,
            entity,
            before,
            cancellationToken);

        var candidate = new Dictionary<string, object?>(before, StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in values)
        {
            candidate[field] = value;
        }

        candidate["updated_by"] = user.UserId;
        await EnsureEffectRecordAllowedAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            mutationAction,
            entity,
            candidate,
            cancellationToken);

        var columns = values.Keys.ToArray();
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        var assignments = columns.Select((column, index) => $"{Quote(column)} = @p{index}").ToList();
        assignments.Add("\"updated_at\" = now()");
        assignments.Add("\"updated_by\" = @updatedBy");
        command.CommandText = $"""
            update {Quote(entity.SchemaName)}.{Quote(entity.TableName)}
            set {string.Join(", ", assignments)}
            where id = @id and archived_at is null
            returning {BuildSelectColumns(entity)}
            """;
        for (var index = 0; index < columns.Length; index++)
        {
            command.Parameters.AddWithValue($"p{index}", values[columns[index]] ?? DBNull.Value);
        }

        command.Parameters.AddWithValue("id", recordId);
        command.Parameters.AddWithValue("updatedBy", user.UserId);

        Dictionary<string, object?> record;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            record = ReadRecord(reader);
        }

        await EnsureEffectRecordAllowedAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            mutationAction,
            entity,
            record,
            cancellationToken);

        await WriteAuditEventAsync(
            connection,
            transaction,
            tenant,
            user,
            "record.updated",
            $"Updated {entity.DisplayName} record.",
            new Dictionary<string, object?>
            {
                ["entity"] = entity.Key,
                ["recordId"] = recordId,
                ["fields"] = columns,
                ["before"] = before,
                ["after"] = record
            },
            cancellationToken);

        return ApplyComputedFields(entity, record);
    }

    private async Task<object?> ConvertValueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        RuntimeFieldDefinition field,
        object? value,
        bool enforceRelationCreateGuards,
        CancellationToken cancellationToken)
    {
        if (IsNullLike(value))
        {
            return null;
        }

        try
        {
            switch (field.FieldType.ToLowerInvariant())
            {
                case "string":
                case "text":
                    return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                case "enum":
                    var enumValue = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                    if (string.IsNullOrWhiteSpace(enumValue) || !field.Options.Contains(enumValue, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new RuntimeActionValidationException($"{field.DisplayName} must be one of: {string.Join(", ", field.Options)}.");
                    }

                    return field.Options.First(option => option.Equals(enumValue, StringComparison.OrdinalIgnoreCase));
                case "integer":
                    if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    {
                        return integer;
                    }

                    throw new RuntimeActionValidationException($"{field.DisplayName} must be an integer.");
                case "number":
                case "decimal":
                case "money":
                    if (decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                    {
                        return number;
                    }

                    throw new RuntimeActionValidationException($"{field.DisplayName} must be numeric.");
                case "boolean":
                    if (value is bool boolean)
                    {
                        return boolean;
                    }

                    if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out boolean))
                    {
                        return boolean;
                    }

                    throw new RuntimeActionValidationException($"{field.DisplayName} must be true or false.");
                case "date":
                    if (value is DateOnly date)
                    {
                        return date;
                    }

                    if (DateOnly.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    {
                        return date;
                    }

                    throw new RuntimeActionValidationException($"{field.DisplayName} must be a valid date.");
                case "datetime":
                    if (value is DateTimeOffset dateTime)
                    {
                        return dateTime;
                    }

                    if (DateTimeOffset.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dateTime))
                    {
                        return dateTime;
                    }

                    throw new RuntimeActionValidationException($"{field.DisplayName} must be a valid date/time.");
                case "relation":
                    if (!long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    {
                        throw new RuntimeActionValidationException($"{field.DisplayName} must be a record ID.");
                    }

                    if (field.ReferencesEntity is not null &&
                        !(enforceRelationCreateGuards &&
                          RuntimeRelationCreateGuardEnforcer.IsGuardedRelation(entity, field)) &&
                        !await RelationExistsAsync(
                            connection,
                            transaction,
                            permissionSnapshot,
                            user,
                            field.ReferencesEntity,
                            id,
                            cancellationToken))
                    {
                        throw new RuntimeActionValidationException($"{field.DisplayName} references a missing record.");
                    }

                    return id;
                default:
                    throw new RuntimeActionValidationException($"{field.DisplayName} uses unsupported type '{field.FieldType}'.");
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new RuntimeActionValidationException(ex.Message);
        }
    }
}
