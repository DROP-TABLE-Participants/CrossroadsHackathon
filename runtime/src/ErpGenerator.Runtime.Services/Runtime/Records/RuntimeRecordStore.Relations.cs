using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeRecordStore
{
    private static async Task<IReadOnlyList<RuntimeLookupItem>> LookupRecordsAsync(
        NpgsqlConnection connection,
        RuntimeEntityDefinition entity,
        string? search,
        int limit,
        RuntimeRecordAccessPredicate predicate,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(limit <= 0 ? 20 : limit, 1, 50);
        var primaryField = ResolveDisplayField(entity, entity.DisplayPrimaryField);
        var secondaryField = ResolveDisplayField(entity, entity.DisplaySecondaryField);
        var labelExpression = BuildDisplayExpression(primaryField);
        var secondaryExpression = secondaryField is null ? "null" : BuildDisplayExpression(secondaryField);
        var where = BuildLookupWhere(entity, primaryField, secondaryField, search, predicate, out var searchParameter);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select id, {labelExpression} as label, {secondaryExpression} as secondary_label
            from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as "r"
            {where.Sql}
            order by label asc, id asc
            limit @limit
            """;
        command.Parameters.AddWithValue("limit", pageSize);
        if (searchParameter is not null)
        {
            command.Parameters.AddWithValue("q", searchParameter);
        }
        predicate.AddParameters(command);

        var items = new List<RuntimeLookupItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var label = reader.IsDBNull(1) ? $"#{id}" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? $"#{id}";
            var secondary = reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture);
            items.Add(new RuntimeLookupItem(id, label, secondary));
        }

        return items;
    }

    private static RuntimeWhere BuildLookupWhere(
        RuntimeEntityDefinition entity,
        RuntimeFieldDefinition? primaryField,
        RuntimeFieldDefinition? secondaryField,
        string? search,
        RuntimeRecordAccessPredicate predicate,
        out string? searchParameter)
    {
        searchParameter = null;
        var clauses = new List<string> { "\"r\".\"archived_at\" is null", predicate.Sql };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var fields = entity.Fields
                .Where(field => field.Computed is null)
                .Where(field => IsTextLike(field) ||
                                field.Name.Equals(primaryField?.Name, StringComparison.OrdinalIgnoreCase) ||
                                field.Name.Equals(secondaryField?.Name, StringComparison.OrdinalIgnoreCase))
                .Where(IsTextLike)
                .Select(field => $"\"r\".{Quote(field.ColumnName)}::text ilike @q")
                .Distinct()
                .ToArray();

            if (fields.Length > 0)
            {
                searchParameter = $"%{search.Trim()}%";
                clauses.Add($"({string.Join(" or ", fields)})");
            }
        }

        return new RuntimeWhere($"where {string.Join(" and ", clauses)}");
    }

    private static RuntimeFieldDefinition? ResolveDisplayField(RuntimeEntityDefinition entity, string? displayField)
    {
        if (string.IsNullOrWhiteSpace(displayField) || displayField.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return entity.Fields.FirstOrDefault(field =>
            field.Computed is null &&
            (field.Name.Equals(displayField, StringComparison.OrdinalIgnoreCase) ||
             field.ColumnName.Equals(displayField, StringComparison.OrdinalIgnoreCase)));
    }

    private static string BuildDisplayExpression(RuntimeFieldDefinition? field) =>
        field is null ? "null" : $"{Quote(field.ColumnName)}::text";

    private static async Task<IReadOnlyDictionary<long, RuntimeLookupItem>> LookupRecordsByIdAsync(
        NpgsqlConnection connection,
        RuntimeEntityDefinition entity,
        IReadOnlyCollection<long> ids,
        RuntimeRecordAccessPredicate predicate,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<long, RuntimeLookupItem>();
        }

        var primaryField = ResolveDisplayField(entity, entity.DisplayPrimaryField);
        var secondaryField = ResolveDisplayField(entity, entity.DisplaySecondaryField);
        var labelExpression = BuildDisplayExpression(primaryField);
        var secondaryExpression = secondaryField is null ? "null" : BuildDisplayExpression(secondaryField);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select id, {labelExpression} as label, {secondaryExpression} as secondary_label
            from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as "r"
            where "r"."id" = any(@ids)
              and "r"."archived_at" is null
              and ({predicate.Sql})
            """;
        command.Parameters.AddWithValue("ids", ids.ToArray());
        predicate.AddParameters(command);

        var items = new Dictionary<long, RuntimeLookupItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var label = reader.IsDBNull(1) ? $"#{id}" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? $"#{id}";
            var secondary = reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture);
            items[id] = new RuntimeLookupItem(id, label, secondary);
        }

        return items;
    }

    private async Task HydrateRelationLabelsAsync(
        NpgsqlConnection connection,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        IReadOnlyList<Dictionary<string, object?>> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        var readableEntity = permissions.FilterReadableEntity(snapshot, entity);
        var relationIndex = 0;
        foreach (var field in readableEntity.Fields.Where(field => field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) && field.ReferencesEntity is not null))
        {
            var target = await LoadEntityByIdAsync(connection, field.ReferencesEntity!, cancellationToken);
            if (target is null)
            {
                continue;
            }

            if (!permissions.IsAllowed(snapshot, "read", target))
            {
                continue;
            }

            var predicate = await permissions.BuildRecordPredicateAsync(
                connection,
                transaction: null,
                snapshot,
                user,
                "read",
                target,
                "r",
                $"relation_label_{relationIndex++}",
                cancellationToken);

            var ids = records
                .Select(record => ToLong(record.GetValueOrDefault(field.ColumnName)))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray();
            var readableTarget = permissions.FilterReadableEntity(snapshot, target);
            var labels = await LookupRecordsByIdAsync(connection, readableTarget, ids, predicate, cancellationToken);

            foreach (var record in records)
            {
                var id = ToLong(record.GetValueOrDefault(field.ColumnName));
                if (!id.HasValue)
                {
                    continue;
                }

                if (labels.TryGetValue(id.Value, out var item))
                {
                    record[$"{field.ColumnName}__label"] = item.Label;
                    record[$"{field.ColumnName}__secondaryLabel"] = item.SecondaryLabel;
                }
                else
                {
                    record[$"{field.ColumnName}__label"] = predicate.AllowsAllRows
                        ? $"#{id.Value} (missing)"
                        : $"#{id.Value}";
                    record[$"{field.ColumnName}__secondaryLabel"] = null;
                }
            }
        }
    }
    private async Task<bool> RelationExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot snapshot,
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

        if (!permissions.IsAllowed(snapshot, "read", entity))
        {
            throw new RuntimeRecordPermissionException("read", entity.Key);
        }

        if (!string.IsNullOrWhiteSpace(requiredReadField) &&
            !permissions.IsFieldAllowed(snapshot, "read", entity, requiredReadField))
        {
            throw new RuntimeRecordPermissionException("read", entity.Key);
        }

        var predicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction,
            snapshot,
            user,
            "read",
            entity,
            "r",
            "relation_input",
            cancellationToken);

        var lockClause = acquireShareLock ? "for share of \"r\"" : string.Empty;
        await using var command = new NpgsqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = $"""
                select {BuildSelectColumns(entity)}
                from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as "r"
                where "r"."id" = @id
                  and "r"."archived_at" is null
                  and ({predicate.Sql})
                {lockClause}
                """
        };
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
                   snapshot,
                   user,
                   "read",
                   entity,
                   targetRecord,
                   cancellationToken);
    }

    private async Task EnsureGuardedRelationAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        IReadOnlyList<RuntimeGuardedRelationLock> guardedRelations,
        CancellationToken cancellationToken)
    {
        foreach (var guarded in guardedRelations)
        {
            if (await RelationExistsAsync(
                    connection,
                    transaction,
                    snapshot,
                    user,
                    guarded.ReferencedEntity,
                    guarded.ReferencedRecordId,
                    cancellationToken,
                    acquireShareLock: false,
                    requiredReadField: guarded.ReferencedField))
            {
                continue;
            }

            throw new RuntimeRecordValidationException(
                [new RuntimeValidationError(
                    guarded.Relation.Name,
                    $"{guarded.Relation.DisplayName} references a missing record.")]);
        }
    }
}
