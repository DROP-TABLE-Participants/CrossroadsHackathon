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
    private async Task<RuntimeActionQueryResult> QueryRecordsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        IReadOnlyList<ActionFilterDefinition> filters,
        RuntimeActionQueryLockMode lockMode,
        RuntimeActionContext context,
        CancellationToken cancellationToken)
    {
        var businessClauses = new List<string> { "archived_at is null" };
        var exactIdentityClauses = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        for (var index = 0; index < filters.Count; index++)
        {
            var filter = filters[index];
            var field = ResolveActionFilterField(entity, filter, context);
            EnsureActionFieldAllowed(permissionSnapshot, "read", entity, field.Name);
            if (filter.ValueFrom is not null)
            {
                EnsureValueFromSourceFieldsAllowed(
                    permissionSnapshot,
                    filter.ValueFrom,
                    context,
                    itemEntity: null);
            }
            var clause = BuildFilterClause(field, filter, context, parameters);
            businessClauses.Add(clause);
            if (IsSystemIdField(field))
            {
                exactIdentityClauses.Add(clause);
            }
        }

        const string recordAlias = "action_records";
        var recordPredicate = await permissions.BuildRecordPredicateAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            "read",
            entity,
            recordAlias,
            "action_query_scope",
            cancellationToken);
        var scopedClauses = businessClauses.Append(recordPredicate.Sql);
        var visibilityClauses = exactIdentityClauses.Count == 0
            ? businessClauses
            : new[] { "archived_at is null" }.Concat(exactIdentityClauses);
        var lockClause = lockMode switch
        {
            RuntimeActionQueryLockMode.Update => $"for update of {Quote(recordAlias)}",
            RuntimeActionQueryLockMode.Share => $"for share of {Quote(recordAlias)}",
            _ => string.Empty
        };

        await using var command = new NpgsqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = $"""
                with scoped_records as materialized (
                  select {BuildSelectColumns(entity, recordAlias)}
                  from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as {Quote(recordAlias)}
                  where {string.Join(" and ", scopedClauses)}
                  order by {Quote(recordAlias)}."id" asc
                  limit @limit
                  {lockClause}
                ), visibility as (
                  select exists (
                    select 1
                    from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as {Quote(recordAlias)}
                    where {string.Join(" and ", visibilityClauses)}
                      and ({recordPredicate.Sql}) is not true
                  ) as has_hidden_matches
                )
                select {BuildSelectColumns(entity, "scoped_records")},
                       "visibility"."has_hidden_matches",
                       "scoped_records"."id" is not null as has_record
                from visibility
                left join scoped_records on true
                order by "scoped_records"."id" asc
                """
        };
        command.Parameters.AddWithValue("limit", MaxActionRecords + 1);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }
        recordPredicate.AddParameters(command);

        var recordColumnCount = 8 + entity.Fields.Count(field => field.Computed is null);
        var records = new List<Dictionary<string, object?>>();
        var hasHiddenMatches = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hasHiddenMatches = reader.GetBoolean(recordColumnCount);
            if (reader.GetBoolean(recordColumnCount + 1))
            {
                records.Add(ReadRecord(reader, recordColumnCount));
            }
        }

        if (records.Count > MaxActionRecords)
        {
            throw new RuntimeActionValidationException($"Action query matched more than {MaxActionRecords} records.");
        }

        return new RuntimeActionQueryResult(entity, records, hasHiddenMatches);
    }

    private static string BuildFilterClause(
        RuntimeFieldDefinition field,
        ActionFilterDefinition filter,
        RuntimeActionContext context,
        List<NpgsqlParameter> parameters)
    {
        var column = Quote(field.ColumnName);
        var op = filter.Op.Trim().ToLowerInvariant();
        if (op == "exists")
        {
            return $"{column} is not null";
        }

        if (op == "not_exists")
        {
            return $"{column} is null";
        }

        object? value = filter.ValueFrom is not null
            ? ResolveValueFrom(filter.ValueFrom, context, null)
            : JsonElementToObject(filter.Value);
        if (IsSystemIdField(field))
        {
            value = RequirePositiveSystemId(value);
        }

        value = NormalizeFilterValue(field, value);
        var values = filter.Values.Count > 0
            ? filter.Values.Select(JsonElementToObject).ToArray()
            : TryGetArrayValue(filter.Value, out var arrayValues)
                ? arrayValues.Select(JsonElementToObject).ToArray()
                : [];
        values = values.Select(value => NormalizeFilterValue(field, value)).ToArray();
        var parameterName = $"p{parameters.Count}";

        if (op is "in" or "not_in")
        {
            var list = values.Length > 0 ? values : value is null ? [] : [value];
            parameters.Add(new NpgsqlParameter(parameterName, list));
            return op == "in"
                ? $"{column} = any(@{parameterName})"
                : $"({column} <> all(@{parameterName}) or {column} is null)";
        }

        parameters.Add(new NpgsqlParameter(parameterName, value ?? DBNull.Value));
        return op switch
        {
            "eq" => value is null ? $"{column} is null" : $"{column} = @{parameterName}",
            "neq" => value is null ? $"{column} is not null" : $"({column} <> @{parameterName} or {column} is null)",
            "gt" => $"{column} > @{parameterName}",
            "gte" => $"{column} >= @{parameterName}",
            "lt" => $"{column} < @{parameterName}",
            "lte" => $"{column} <= @{parameterName}",
            _ => throw new RuntimeActionValidationException($"Unsupported filter operator '{filter.Op}'.")
        };
    }

    private static object? NormalizeFilterValue(RuntimeFieldDefinition field, object? value)
    {
        if (IsNullLike(value))
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
            _ => value
        };
    }

    private static RuntimeFieldDefinition ResolveActionFilterField(
        RuntimeEntityDefinition entity,
        ActionFilterDefinition filter,
        RuntimeActionContext context)
    {
        if (!string.Equals(filter.Field, "id", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveField(entity, filter.Field)
                   ?? throw new RuntimeActionValidationException(
                       $"Action filter references unknown field '{filter.Field}'.");
        }

        ValidateSystemIdFilter(entity, filter, context);
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

    private static void ValidateSystemIdFilter(
        RuntimeEntityDefinition targetEntity,
        ActionFilterDefinition filter,
        RuntimeActionContext context)
    {
        if (!string.Equals(filter.Op, "eq", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeActionValidationException(
                "Action system id filter must use exact operator 'eq'.");
        }

        if (filter.Values.Count > 0 || TryGetArrayValue(filter.Value, out _))
        {
            throw new RuntimeActionValidationException(
                "Action system id filter must compare one exact identity, not a value list.");
        }

        var valueFrom = filter.ValueFrom;
        if (filter.Value is not null || valueFrom is null)
        {
            throw new RuntimeActionValidationException(
                "Action system id filter must use one relation-bound value_from.record identity; literal ids are forbidden.");
        }

        if (string.IsNullOrWhiteSpace(valueFrom.Record) ||
            !string.IsNullOrWhiteSpace(valueFrom.Item) ||
            !string.IsNullOrWhiteSpace(valueFrom.Step) ||
            !string.IsNullOrWhiteSpace(valueFrom.Field) ||
            !string.IsNullOrWhiteSpace(valueFrom.Now))
        {
            throw new RuntimeActionValidationException(
                "Action system id filter value_from must use one record field.");
        }

        if (string.Equals(valueFrom.Record, "id", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.SourceEntity.Id.Equals(targetEntity.Id, StringComparison.Ordinal))
            {
                throw new RuntimeActionValidationException(
                    "Action system id filter record.id does not identify the queried entity.");
            }

            return;
        }

        var sourceField = ResolveField(context.SourceEntity, valueFrom.Record)
                          ?? throw new RuntimeActionValidationException(
                              $"Action system id filter references unknown source field '{valueFrom.Record}'.");
        if (!sourceField.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceField.ReferencesEntity, targetEntity.Id, StringComparison.Ordinal))
        {
            throw new RuntimeActionValidationException(
                $"Action system id filter source field '{valueFrom.Record}' does not identify entity '{targetEntity.Id}'.");
        }
    }

    private static bool IsSystemIdField(RuntimeFieldDefinition field) =>
        field.Id.Equals("id", StringComparison.Ordinal) &&
        field.Name.Equals("id", StringComparison.Ordinal) &&
        field.ColumnName.Equals("id", StringComparison.Ordinal);

    private static long RequirePositiveSystemId(object? value)
    {
        var id = value switch
        {
            byte item => (long)item,
            sbyte item => (long)item,
            short item => (long)item,
            ushort item => (long)item,
            int item => (long)item,
            uint item => (long)item,
            long item => item,
            ulong item when item <= long.MaxValue => (long)item,
            _ => 0L
        };
        if (id <= 0)
        {
            throw new RuntimeActionValidationException(
                "Action system id filter value must be a positive 64-bit integer.");
        }

        return id;
    }
}
