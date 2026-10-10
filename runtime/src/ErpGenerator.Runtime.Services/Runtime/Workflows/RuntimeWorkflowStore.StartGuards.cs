using System.Globalization;
using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeWorkflowStore
{
    private const int MaxWorkflowStartGuardMatches = ErpWorkflowStartGuardLimits.MaxResultCount;

    private static async Task EvaluateStartGuardsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeWorkflowDefinition workflow,
        RuntimeEntityDefinition sourceEntity,
        IReadOnlyDictionary<string, object?> sourceRecord,
        CancellationToken cancellationToken)
    {
        await LockStartGuardCorrelationParentsAsync(
            connection,
            transaction,
            workflow,
            sourceEntity,
            sourceRecord,
            cancellationToken);

        var results = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var guard in workflow.StartGuards
                     .OrderBy(candidate => candidate.Entity, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            ValidateStartGuardIdentity(guard);
            var targetEntity = await LoadEntityByIdAsync(connection, guard.Entity, cancellationToken)
                               ?? throw new RuntimeWorkflowValidationException(
                                   $"Workflow start guard '{guard.Id}' references unknown entity '{guard.Entity}'.");
            var count = await LockAndCountStartGuardMatchesAsync(
                connection,
                transaction,
                guard,
                sourceEntity,
                sourceRecord,
                targetEntity,
                cancellationToken);
            if (!results.TryAdd(guard.Id, count))
            {
                throw new RuntimeWorkflowValidationException(
                    $"Workflow contains duplicate start guard id '{guard.Id}'.");
            }
        }

        // Guard-row acquisition is canonical to minimize lock cycles. The
        // outer workflow-start boundary also retries PostgreSQL deadlocks that
        // can arise from inverse source/parent relationships. Authored guard
        // order remains the observable business contract when more than one
        // precondition fails.
        foreach (var guard in workflow.StartGuards)
        {
            if (!results.TryGetValue(guard.Id, out var count) ||
                count is null ||
                !CompareStartGuardCount(count.Value, guard.ResultCountOp, guard.ResultCount))
            {
                throw new RuntimeWorkflowStartGuardRejectedException(guard.Id);
            }
        }
    }

    private static async Task LockStartGuardCorrelationParentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeWorkflowDefinition workflow,
        RuntimeEntityDefinition sourceEntity,
        IReadOnlyDictionary<string, object?> sourceRecord,
        CancellationToken cancellationToken)
    {
        var parentLocks = new Dictionary<(string EntityId, long RecordId), HashSet<string>>();
        foreach (var guard in workflow.StartGuards
                     .OrderBy(candidate => candidate.Entity, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            var targetEntity = await LoadEntityByIdAsync(connection, guard.Entity, cancellationToken)
                               ?? throw new RuntimeWorkflowValidationException(
                                   $"Workflow start guard '{guard.Id}' references unknown entity '{guard.Entity}'.");
            foreach (var filter in guard.Where.Where(filter =>
                         filter.Op.Equals("eq", StringComparison.Ordinal) &&
                         filter.ValueFrom is { Record: { Length: > 0 } }))
            {
                var sourceField = ResolveExactStartGuardSourceField(
                    sourceEntity,
                    filter.ValueFrom!.Record!);
                var targetField = ResolveExactStartGuardTargetField(targetEntity, filter.Field);
                if (sourceField is null ||
                    !sourceField.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(sourceField.ReferencesEntity) ||
                    !ExactStartGuardFieldsCorrelate(sourceEntity, sourceField, targetEntity, targetField))
                {
                    continue;
                }

                var rawParentId = sourceRecord.GetValueOrDefault(sourceField.ColumnName);
                if (rawParentId is null or DBNull)
                {
                    // Optional correlations are allowed by the schema. There
                    // is no parent row to coordinate in this case; the guard
                    // evaluator below rejects the missing correlation through
                    // the same typed business-rule boundary as a zero match.
                    continue;
                }

                if (!long.TryParse(
                        Convert.ToString(rawParentId, CultureInfo.InvariantCulture),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parentId) ||
                    parentId <= 0)
                {
                    throw new RuntimeWorkflowValidationException(
                        $"Workflow start guard '{guard.Id}' has an invalid relation correlation value.");
                }

                var key = (sourceField.ReferencesEntity!, parentId);
                if (!parentLocks.TryGetValue(key, out var guardIds))
                {
                    guardIds = new HashSet<string>(StringComparer.Ordinal);
                    parentLocks[key] = guardIds;
                }

                guardIds.Add(guard.Id);
            }
        }

        var missingGuardIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ((entityId, recordId), guardIds) in parentLocks
                     .OrderBy(item => item.Key.EntityId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.RecordId))
        {
            var parentEntity = await LoadEntityByIdAsync(connection, entityId, cancellationToken)
                               ?? throw new RuntimeWorkflowValidationException(
                                   "Workflow start guard relation parent entity was not found.");
            if (await ReadRecordByIdAsync(
                    connection,
                    parentEntity,
                    recordId,
                    cancellationToken,
                    transaction,
                    forUpdate: true) is null)
            {
                missingGuardIds.UnionWith(guardIds);
            }
        }


        var firstMissingGuard = workflow.StartGuards.FirstOrDefault(guard =>
            missingGuardIds.Contains(guard.Id));
        if (firstMissingGuard is not null)
        {
            throw new RuntimeWorkflowStartGuardRejectedException(firstMissingGuard.Id);
        }
    }

    private static void ValidateStartGuardIdentity(RuntimeWorkflowStartGuard guard)
    {
        if (string.IsNullOrWhiteSpace(guard.Id) ||
            !IdentifierPattern.IsMatch(guard.Id) ||
            string.IsNullOrWhiteSpace(guard.Entity) ||
            !IdentifierPattern.IsMatch(guard.Entity) ||
            guard.Where.Count == 0 ||
            guard.ResultCount is < 0 or > MaxWorkflowStartGuardMatches ||
            guard.ResultCountOp is not ("eq" or "neq" or "gt" or "gte" or "lt" or "lte"))
        {
            throw new RuntimeWorkflowValidationException("Workflow contains an invalid start guard definition.");
        }
    }

    private static async Task<int?> LockAndCountStartGuardMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeWorkflowStartGuard guard,
        RuntimeEntityDefinition sourceEntity,
        IReadOnlyDictionary<string, object?> sourceRecord,
        RuntimeEntityDefinition targetEntity,
        CancellationToken cancellationToken)
    {
        var clauses = new List<string> { "archived_at is null" };
        var parameters = new List<NpgsqlParameter>();
        var hasExactCorrelation = false;
        var missingExactCorrelation = false;
        foreach (var filter in guard.Where)
        {
            var targetField = ResolveExactStartGuardTargetField(targetEntity, filter.Field);
            var op = filter.Op.Trim().ToLowerInvariant();
            if (op is not ("eq" or "neq" or "gt" or "gte" or "lt" or "lte") ||
                filter.Values.Count != 0)
            {
                throw new RuntimeWorkflowValidationException(
                    $"Workflow start guard '{guard.Id}' contains an unsupported filter.");
            }

            var hasLiteral = filter.Value is not null &&
                             filter.Value is not JsonElement { ValueKind: JsonValueKind.Null };
            var hasValueFrom = filter.ValueFrom is not null;
            if (hasLiteral == hasValueFrom)
            {
                throw new RuntimeWorkflowValidationException(
                    $"Workflow start guard '{guard.Id}' filters require exactly one operand.");
            }

            object? value;
            if (hasValueFrom)
            {
                var valueFrom = filter.ValueFrom!;
                if (string.IsNullOrWhiteSpace(valueFrom.Record) ||
                    !string.IsNullOrWhiteSpace(valueFrom.Item) ||
                    !string.IsNullOrWhiteSpace(valueFrom.Step) ||
                    !string.IsNullOrWhiteSpace(valueFrom.Field) ||
                    !string.IsNullOrWhiteSpace(valueFrom.Now))
                {
                    throw new RuntimeWorkflowValidationException(
                        $"Workflow start guard '{guard.Id}' value_from must use one current-record field.");
                }

                var sourceField = ResolveExactStartGuardSourceField(sourceEntity, valueFrom.Record!);
                value = sourceField is null
                    ? sourceRecord.GetValueOrDefault("id")
                    : sourceRecord.GetValueOrDefault(sourceField.ColumnName);
                if (value is null or DBNull)
                {
                    // A missing correlation value cannot accidentally match a
                    // target row; preserve SQL null semantics deterministically.
                    value = null;
                }

                if (op.Equals("eq", StringComparison.Ordinal) &&
                    ExactStartGuardFieldsCorrelate(sourceEntity, sourceField, targetEntity, targetField))
                {
                    hasExactCorrelation = true;
                    if (value is null or DBNull)
                    {
                        missingExactCorrelation = true;
                    }
                }

                ValidateOrderedStartGuardComparison(guard.Id, op, sourceField, targetField);
            }
            else
            {
                value = StartGuardJsonValue(filter.Value);
            }

            value = NormalizeStartGuardValue(targetField, value);
            var parameterName = $"p{parameters.Count}";
            parameters.Add(new NpgsqlParameter(parameterName, value ?? DBNull.Value));
            var column = Quote(targetField.ColumnName);
            clauses.Add(op switch
            {
                "eq" => value is null ? $"{column} is null" : $"{column} = @{parameterName}",
                "neq" => value is null ? $"{column} is not null" : $"({column} <> @{parameterName} or {column} is null)",
                "gt" => $"{column} > @{parameterName}",
                "gte" => $"{column} >= @{parameterName}",
                "lt" => $"{column} < @{parameterName}",
                "lte" => $"{column} <= @{parameterName}",
                _ => throw new RuntimeWorkflowValidationException(
                    $"Workflow start guard '{guard.Id}' uses unsupported operator '{op}'.")
            });
        }

        if (!hasExactCorrelation)
        {
            throw new RuntimeWorkflowValidationException(
                $"Workflow start guard '{guard.Id}' is not exactly correlated to its current record.");
        }

        if (missingExactCorrelation)
        {
            return null;
        }

        await using var command = new NpgsqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = $"""
                select id
                from {Quote(targetEntity.SchemaName)}.{Quote(targetEntity.TableName)}
                where {string.Join(" and ", clauses)}
                order by id asc
                limit @guardLimit
                for update
                """
        };
        command.Parameters.AddWithValue("guardLimit", MaxWorkflowStartGuardMatches + 1);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
        }

        // The query deliberately saturates at Max + 1. Every authored
        // threshold is bounded to Max, so that sentinel preserves exact
        // eq/neq/ordered comparison truth without scanning an unbounded set
        // or turning a valid high-cardinality business condition into a
        // metadata-validation failure.
        return count;
    }

    private static RuntimeFieldDefinition ResolveExactStartGuardTargetField(
        RuntimeEntityDefinition entity,
        string fieldId)
    {
        if (fieldId.Equals("id", StringComparison.Ordinal))
        {
            return new RuntimeFieldDefinition(
                "id",
                entity.Id,
                "id",
                "ID",
                "integer",
                "id",
                "bigint",
                Required: true,
                Unique: true,
                ReferencesEntity: entity.Id,
                Options: [],
                Computed: null);
        }

        var persistedFieldId = $"{entity.Id}.{fieldId}";
        return entity.Fields.SingleOrDefault(field => field.Id.Equals(persistedFieldId, StringComparison.Ordinal) &&
                                                       field.Computed is null)
               ?? throw new RuntimeWorkflowValidationException(
                   $"Workflow start guard references unknown, aliased, or computed field '{entity.Id}.{fieldId}'.");
    }

    private static RuntimeFieldDefinition? ResolveExactStartGuardSourceField(
        RuntimeEntityDefinition entity,
        string fieldId)
    {
        if (fieldId.Equals("id", StringComparison.Ordinal))
        {
            return null;
        }

        var persistedFieldId = $"{entity.Id}.{fieldId}";
        return entity.Fields.SingleOrDefault(field => field.Id.Equals(persistedFieldId, StringComparison.Ordinal) &&
                                                       field.Computed is null)
               ?? throw new RuntimeWorkflowValidationException(
                   $"Workflow start guard references unknown, aliased, or computed current-record field '{entity.Id}.{fieldId}'.");
    }

    private static bool ExactStartGuardFieldsCorrelate(
        RuntimeEntityDefinition sourceEntity,
        RuntimeFieldDefinition? sourceField,
        RuntimeEntityDefinition targetEntity,
        RuntimeFieldDefinition targetField)
    {
        var sourceReference = sourceField is null
            ? sourceEntity.Id
            : sourceField.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase)
                ? sourceField.ReferencesEntity
                : null;
        var targetReference = targetField.Id.Equals("id", StringComparison.Ordinal)
            ? targetEntity.Id
            : targetField.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase)
                ? targetField.ReferencesEntity
                : null;
        return !string.IsNullOrWhiteSpace(sourceReference) &&
               sourceReference.Equals(targetReference, StringComparison.Ordinal);
    }

    private static void ValidateOrderedStartGuardComparison(
        string guardId,
        string op,
        RuntimeFieldDefinition? sourceField,
        RuntimeFieldDefinition targetField)
    {
        if (op is not ("gt" or "gte" or "lt" or "lte"))
        {
            return;
        }

        var supported = new HashSet<string>(
            ["integer", "number", "decimal", "money", "date", "datetime"],
            StringComparer.OrdinalIgnoreCase);
        if (sourceField is null ||
            !supported.Contains(sourceField.FieldType) ||
            !supported.Contains(targetField.FieldType) ||
            !sourceField.FieldType.Equals(targetField.FieldType, StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeWorkflowValidationException(
                $"Workflow start guard '{guardId}' ordered comparisons require exact matching materialized numeric or temporal field types.");
        }
    }

    private static object? StartGuardJsonValue(object? value) => value is JsonElement element
        ? element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new RuntimeWorkflowValidationException(
                "Workflow start guard literal must be a scalar value.")
        }
        : value;

    private static object? NormalizeStartGuardValue(RuntimeFieldDefinition field, object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return field.FieldType.ToLowerInvariant() switch
        {
            "boolean" when value is bool boolean => boolean,
            "boolean" when bool.TryParse(text, out var boolean) => boolean,
            "integer" or "relation" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            "number" or "decimal" or "money" when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
            "date" when value is DateOnly date => date,
            "date" when DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date,
            "datetime" when value is DateTimeOffset dateTime => dateTime,
            "datetime" when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime) => dateTime,
            "enum" when field.Options.Contains(text!, StringComparer.Ordinal) => text,
            "string" or "text" => text,
            _ => throw new RuntimeWorkflowValidationException(
                $"Workflow start guard value is incompatible with field '{field.EntityId}.{field.Id}'.")
        };
    }

    private static bool CompareStartGuardCount(int actual, string op, int expected) => op switch
    {
        "eq" => actual == expected,
        "neq" => actual != expected,
        "gt" => actual > expected,
        "gte" => actual >= expected,
        "lt" => actual < expected,
        "lte" => actual <= expected,
        _ => false
    };
}
