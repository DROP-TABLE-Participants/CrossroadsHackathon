using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace ErpGenerator.Runtime.Services.Runtime;

internal static partial class RuntimeRelationCreateGuardEnforcer
{
    public static async Task<IReadOnlyList<RuntimeGuardedRelationLock>> EnsureAllowedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> candidate,
        Func<IReadOnlyList<RuntimeGuardedRelationLock>, CancellationToken, Task> ensureAuthorizedAsync,
        CancellationToken cancellationToken)
    {
        var requests = new List<GuardRequest>();
        foreach (var guard in entity.RelationCreateGuards)
        {
            var relation = ResolveRelationField(entity, guard.RelationField);
            if (relation is null ||
                !relation.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(relation.ReferencesEntity, guard.ReferencedEntity, StringComparison.OrdinalIgnoreCase) ||
                !candidate.TryGetValue(relation.ColumnName, out var rawReference) ||
                rawReference is null or DBNull)
            {
                throw Rejected(entity, guard);
            }

            long referencedRecordId;
            try
            {
                referencedRecordId = Convert.ToInt64(rawReference, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                throw Rejected(entity, guard);
            }

            if (referencedRecordId <= 0 ||
                !guard.Op.Equals("eq", StringComparison.Ordinal) ||
                !guard.ValueType.Equals("string", StringComparison.Ordinal))
            {
                throw Rejected(entity, guard);
            }

            requests.Add(new GuardRequest(relation, referencedRecordId, guard));
        }

        var locks = requests
            .Select(request => new RuntimeGuardedRelationLock(
                request.Relation,
                request.Guard.ReferencedEntity,
                request.ReferencedRecordId,
                request.Guard.ReferencedField))
            .DistinctBy(item => (
                item.Relation.ColumnName,
                item.ReferencedEntity,
                item.ReferencedRecordId,
                item.ReferencedField))
            .OrderBy(item => item.ReferencedEntity, StringComparer.Ordinal)
            .ThenBy(item => item.ReferencedRecordId)
            .ThenBy(item => item.Relation.ColumnName, StringComparer.Ordinal)
            .ThenBy(item => item.ReferencedField, StringComparer.Ordinal)
            .ToArray();
        await ensureAuthorizedAsync(locks, cancellationToken);

        var evaluations = new List<GuardEvaluation>();
        foreach (var group in requests
                     .GroupBy(request => new GuardTargetKey(
                         request.Guard.ReferencedEntity,
                         request.ReferencedRecordId))
                     .OrderBy(group => group.Key.ReferencedEntity, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.ReferencedRecordId))
        {
            var resolved = new List<ResolvedGuardRequest>();
            foreach (var request in group)
            {
                var target = await ResolveTargetAsync(
                    connection,
                    transaction,
                    request.Guard.ReferencedEntity,
                    request.Guard.ReferencedField,
                    cancellationToken);
                if (target is null)
                {
                    throw new InvalidOperationException(
                        $"Relation create guard metadata references missing field '{request.Guard.ReferencedEntity}.{request.Guard.ReferencedField}'.");
                }

                resolved.Add(new ResolvedGuardRequest(request, target));
            }

            var tableName = resolved[0].Target.TableName;
            if (resolved.Any(request => !request.Target.TableName.Equals(tableName, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Relation create guard metadata for '{group.Key.ReferencedEntity}' resolved to inconsistent tables.");
            }

            EnsureIdentifier(tableName);
            var columns = resolved
                .Select(request => request.Target.ColumnName)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (var column in columns)
            {
                EnsureIdentifier(column);
            }

            await using var command = new NpgsqlCommand(
                $"select {string.Join(", ", columns.Select(Quote))} from {Quote("app")}.{Quote(tableName)} " +
                "where id = @recordId and archived_at is null for update",
                connection,
                transaction);
            command.Parameters.AddWithValue("recordId", group.Key.ReferencedRecordId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                evaluations.AddRange(resolved.Select(request =>
                    new GuardEvaluation(request.Request.Guard, null)));
                continue;
            }

            var ordinalByColumn = columns
                .Select((column, ordinal) => (column, ordinal))
                .ToDictionary(item => item.column, item => item.ordinal, StringComparer.Ordinal);
            foreach (var request in resolved)
            {
                var actual = reader.GetValue(ordinalByColumn[request.Target.ColumnName]);
                evaluations.Add(new GuardEvaluation(
                    request.Request.Guard,
                    actual is DBNull ? null : Convert.ToString(actual, CultureInfo.InvariantCulture)));
            }
        }

        // Re-evaluate authorization while the referenced rows are held. The first
        // check prevents guard-state disclosure; this second check closes the
        // authorization TOCTOU window before any guarded value is interpreted.
        await ensureAuthorizedAsync(locks, cancellationToken);
        foreach (var evaluation in evaluations)
        {
            if (!string.Equals(
                    evaluation.Actual,
                    evaluation.Guard.Value,
                    StringComparison.Ordinal))
            {
                throw Rejected(entity, evaluation.Guard);
            }
        }

        return locks;
    }

    public static bool IsGuardedRelation(
        RuntimeEntityDefinition entity,
        RuntimeFieldDefinition field)
    {
        var canonicalId = CanonicalFieldId(entity, field);
        return entity.RelationCreateGuards.Any(guard =>
            guard.RelationField.Equals(canonicalId, StringComparison.Ordinal));
    }

    private static RuntimeFieldDefinition? ResolveRelationField(
        RuntimeEntityDefinition entity,
        string canonicalFieldId)
    {
        return entity.Fields.SingleOrDefault(field =>
            CanonicalFieldId(entity, field).Equals(canonicalFieldId, StringComparison.Ordinal));
    }

    private static string CanonicalFieldId(
        RuntimeEntityDefinition entity,
        RuntimeFieldDefinition field)
    {
        var prefix = $"{entity.Id}.";
        return field.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? field.Id[prefix.Length..]
            : field.Id;
    }

    private static async Task<GuardTarget?> ResolveTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string entityId,
        string fieldId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select e.table_name, f.column_name
            from _runtime.entity_definitions e
            join _runtime.field_definitions f
              on f.entity_id = e.id
             and f.id = e.id || '.' || @fieldId
            where e.id = @entityId
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("entityId", entityId);
        command.Parameters.AddWithValue("fieldId", fieldId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GuardTarget(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static RuntimeRelationCreateGuardRejectedException Rejected(
        RuntimeEntityDefinition entity,
        RuntimeRelationCreateGuard guard) => new(
        entity.Id,
        guard.RelationField,
        guard.ReferencedEntity,
        guard.ReferencedField);

    private static void EnsureIdentifier(string value)
    {
        if (!IdentifierPattern().IsMatch(value))
        {
            throw new InvalidOperationException($"Unsafe runtime identifier '{value}'.");
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

    private sealed record GuardTarget(string TableName, string ColumnName);

    private sealed record GuardTargetKey(string ReferencedEntity, long ReferencedRecordId);

    private sealed record GuardRequest(
        RuntimeFieldDefinition Relation,
        long ReferencedRecordId,
        RuntimeRelationCreateGuard Guard);

    private sealed record ResolvedGuardRequest(GuardRequest Request, GuardTarget Target);

    private sealed record GuardEvaluation(RuntimeRelationCreateGuard Guard, string? Actual);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

internal sealed record RuntimeGuardedRelationLock(
    RuntimeFieldDefinition Relation,
    string ReferencedEntity,
    long ReferencedRecordId,
    string ReferencedField);
