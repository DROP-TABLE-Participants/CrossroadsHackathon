using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimePermissionService
{
    private static readonly Regex RecordScopeIdentifierPattern =
        new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal bool HasApplicableRecordScopes(
        RuntimePermissionSnapshot snapshot,
        string action,
        RuntimeEntityDefinition entity)
    {
        if (snapshot.HasSystemOwnerAuthority || string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        var matching = snapshot.Grants
            .Where(grant => ActionMatches(grant.Action, action.Trim().ToLowerInvariant()) &&
                            EntityMatches(grant.Entity, entity))
            .ToArray();
        return !matching.Any(grant => grant.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.Ordinal)) &&
               matching.Any(grant => grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.Ordinal));
    }

    internal async Task<RuntimeRecordAccessPredicate> BuildRecordPredicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        string action,
        RuntimeEntityDefinition entity,
        string sqlAlias,
        string parameterPrefix,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeRecordScopeIdentifier(sqlAlias) || !IsSafeRecordScopeIdentifier(parameterPrefix))
        {
            return RuntimeRecordAccessPredicate.DenyAll;
        }

        if (snapshot.HasSystemOwnerAuthority)
        {
            return RuntimeRecordAccessPredicate.AllowAll;
        }

        var normalizedAction = action.Trim().ToLowerInvariant();
        var matching = snapshot.Grants
            .Where(grant => ActionMatches(grant.Action, normalizedAction) && EntityMatches(grant.Entity, entity))
            .ToArray();
        if (matching.Any(grant => grant.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.Ordinal)))
        {
            return RuntimeRecordAccessPredicate.AllowAll;
        }

        var clauses = new List<string>();
        var parameters = new List<RuntimeRecordAccessParameter>();
        var recordGrants = matching
            .Where(grant => grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.Ordinal))
            .ToArray();
        for (var index = 0; index < recordGrants.Length; index++)
        {
            var clause = await TryBuildRecordGrantPredicateAsync(
                connection,
                transaction,
                recordGrants[index],
                user,
                entity,
                sqlAlias,
                $"{parameterPrefix}_{index}",
                parameters,
                cancellationToken);
            if (clause is not null)
            {
                clauses.Add(clause);
            }
        }

        return clauses.Count == 0
            ? RuntimeRecordAccessPredicate.DenyAll
            : new RuntimeRecordAccessPredicate(
                $"({string.Join(" or ", clauses)})",
                parameters,
                AllowsAllRows: false,
                DeniesAllRows: false);
    }

    internal async Task<bool> IsCandidateRecordAllowedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        string action,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> candidate,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.HasSystemOwnerAuthority)
        {
            return true;
        }

        var normalizedAction = action.Trim().ToLowerInvariant();
        var matching = snapshot.Grants
            .Where(grant => ActionMatches(grant.Action, normalizedAction) && EntityMatches(grant.Entity, entity))
            .ToArray();
        if (matching.Any(grant => grant.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.Ordinal)))
        {
            return true;
        }

        foreach (var grant in matching.Where(grant =>
                     grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.Ordinal)))
        {
            if (await CandidateMatchesRecordGrantAsync(
                    connection,
                    transaction,
                    grant,
                    user,
                    entity,
                    candidate,
                    cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string?> TryBuildRecordGrantPredicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimePermissionGrant grant,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        string sqlAlias,
        string parameterPrefix,
        List<RuntimeRecordAccessParameter> parameters,
        CancellationToken cancellationToken)
    {
        var scope = grant.RecordScope;
        if (scope is null || !RecordPermissionScopeSources.Supported.Contains(scope.Source))
        {
            return null;
        }

        var governed = ResolveGovernedRecordScopeField(entity, scope);
        if (governed is null)
        {
            return null;
        }

        var qualifiedField = $"{QuoteRecordScopeIdentifier(sqlAlias)}.{QuoteRecordScopeIdentifier(governed.ColumnName)}";
        if (scope.Source.Equals(RecordPermissionScopeSources.ActorPrincipal, StringComparison.Ordinal))
        {
            if (!governed.ActorPrincipalCompatible ||
                scope.LinkedEntity is not null ||
                scope.LinkedField is not null)
            {
                return null;
            }

            var userParameter = $"{parameterPrefix}_principal";
            parameters.Add(new RuntimeRecordAccessParameter(userParameter, user.UserId));
            return $"{qualifiedField} = @{userParameter}";
        }

        if (!governed.LinkedRecordCompatible ||
            string.IsNullOrWhiteSpace(governed.ReferencesEntity) ||
            string.IsNullOrWhiteSpace(scope.LinkedEntity))
        {
            return null;
        }

        var linkedEntity = await LoadRecordScopeEntityAsync(
            connection,
            transaction,
            scope.LinkedEntity,
            cancellationToken);
        if (linkedEntity is null || !linkedEntity.Id.Equals(scope.LinkedEntity, StringComparison.Ordinal))
        {
            return null;
        }

        var userIdParameter = $"{parameterPrefix}_user";
        var linkedEntityParameter = $"{parameterPrefix}_entity";
        parameters.Add(new RuntimeRecordAccessParameter(userIdParameter, user.UserId));
        parameters.Add(new RuntimeRecordAccessParameter(linkedEntityParameter, linkedEntity.Id));
        const string linkAlias = "\"scope_link\"";
        const string linkedAlias = "\"scope_linked\"";
        var linkedTable = $"\"app\".{QuoteRecordScopeIdentifier(linkedEntity.TableName)}";

        if (scope.LinkedField is null)
        {
            if (!governed.ReferencesEntity.Equals(linkedEntity.Id, StringComparison.Ordinal))
            {
                return null;
            }

            return $"""
                exists (
                  select 1
                  from _runtime.runtime_user_links as {linkAlias}
                  join {linkedTable} as {linkedAlias}
                    on {linkedAlias}."id" = {linkAlias}."record_id"
                   and {linkedAlias}."archived_at" is null
                  where {linkAlias}."runtime_user_id" = @{userIdParameter}
                    and {linkAlias}."entity_key" = @{linkedEntityParameter}
                    and (
                      select count(*)
                      from _runtime.runtime_user_links as "scope_link_count"
                      where "scope_link_count"."runtime_user_id" = @{userIdParameter}
                        and "scope_link_count"."entity_key" = @{linkedEntityParameter}
                    ) = 1
                    and {qualifiedField} is not null
                    and {qualifiedField} = {linkAlias}."record_id"
                )
                """;
        }

        var linkedField = FindSingleRecordScopeField(linkedEntity.Fields, scope.LinkedField);
        if (linkedField is null ||
            linkedField.Computed ||
            !linkedField.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(linkedField.ReferencesEntity) ||
            !linkedField.ReferencesEntity.Equals(governed.ReferencesEntity, StringComparison.Ordinal))
        {
            return null;
        }

        var targetEntity = await LoadRecordScopeEntityAsync(
            connection,
            transaction,
            governed.ReferencesEntity,
            cancellationToken);
        if (targetEntity is null || !targetEntity.Id.Equals(governed.ReferencesEntity, StringComparison.Ordinal))
        {
            return null;
        }

        const string targetAlias = "\"scope_target\"";
        var targetTable = $"\"app\".{QuoteRecordScopeIdentifier(targetEntity.TableName)}";
        var linkedColumn = QuoteRecordScopeIdentifier(linkedField.ColumnName);
        return $"""
            exists (
              select 1
              from _runtime.runtime_user_links as {linkAlias}
              join {linkedTable} as {linkedAlias}
                on {linkedAlias}."id" = {linkAlias}."record_id"
               and {linkedAlias}."archived_at" is null
              join {targetTable} as {targetAlias}
                on {targetAlias}."id" = {linkedAlias}.{linkedColumn}
               and {targetAlias}."archived_at" is null
              where {linkAlias}."runtime_user_id" = @{userIdParameter}
                and {linkAlias}."entity_key" = @{linkedEntityParameter}
                and (
                  select count(*)
                  from _runtime.runtime_user_links as "scope_link_count"
                  where "scope_link_count"."runtime_user_id" = @{userIdParameter}
                    and "scope_link_count"."entity_key" = @{linkedEntityParameter}
                ) = 1
                and {qualifiedField} is not null
                and {qualifiedField} = {targetAlias}."id"
            )
            """;
    }

    private static async Task<bool> CandidateMatchesRecordGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimePermissionGrant grant,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> candidate,
        CancellationToken cancellationToken)
    {
        var scope = grant.RecordScope;
        if (scope is null || !RecordPermissionScopeSources.Supported.Contains(scope.Source))
        {
            return false;
        }

        var governed = ResolveGovernedRecordScopeField(entity, scope);
        if (governed is null || !TryReadCandidateValue(candidate, governed, out var candidateValue))
        {
            return false;
        }

        if (scope.Source.Equals(RecordPermissionScopeSources.ActorPrincipal, StringComparison.Ordinal))
        {
            return governed.ActorPrincipalCompatible &&
                   scope.LinkedEntity is null &&
                   scope.LinkedField is null &&
                   ToInt64(candidateValue) == user.UserId;
        }

        if (!governed.LinkedRecordCompatible ||
            string.IsNullOrWhiteSpace(governed.ReferencesEntity) ||
            string.IsNullOrWhiteSpace(scope.LinkedEntity) ||
            ToInt64(candidateValue) is not { } governedRecordId)
        {
            return false;
        }

        var linkedEntity = await LoadRecordScopeEntityAsync(
            connection,
            transaction,
            scope.LinkedEntity,
            cancellationToken);
        if (linkedEntity is null || !linkedEntity.Id.Equals(scope.LinkedEntity, StringComparison.Ordinal))
        {
            return false;
        }

        RuntimeRecordScopeField? linkedField = null;
        RuntimeRecordScopeEntity? targetEntity = null;
        if (scope.LinkedField is null)
        {
            if (!governed.ReferencesEntity.Equals(linkedEntity.Id, StringComparison.Ordinal))
            {
                return false;
            }
        }
        else
        {
            linkedField = FindSingleRecordScopeField(linkedEntity.Fields, scope.LinkedField);
            if (linkedField is null ||
                linkedField.Computed ||
                !linkedField.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(linkedField.ReferencesEntity) ||
                !linkedField.ReferencesEntity.Equals(governed.ReferencesEntity, StringComparison.Ordinal))
            {
                return false;
            }

            targetEntity = await LoadRecordScopeEntityAsync(
                connection,
                transaction,
                governed.ReferencesEntity,
                cancellationToken);
            if (targetEntity is null || !targetEntity.Id.Equals(governed.ReferencesEntity, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var linkAlias = "scope_link";
        var linkedAlias = "scope_linked";
        var sql = scope.LinkedField is null
            ? $"""
                select 1
                from _runtime.runtime_user_links as "{linkAlias}"
                join "app".{QuoteRecordScopeIdentifier(linkedEntity.TableName)} as "{linkedAlias}"
                  on "{linkedAlias}"."id" = "{linkAlias}"."record_id"
                 and "{linkedAlias}"."archived_at" is null
                where "{linkAlias}"."runtime_user_id" = @scopeUserId
                  and "{linkAlias}"."entity_key" = @scopeEntityId
                  and (
                    select count(*)
                    from _runtime.runtime_user_links as "scope_link_count"
                    where "scope_link_count"."runtime_user_id" = @scopeUserId
                      and "scope_link_count"."entity_key" = @scopeEntityId
                  ) = 1
                  and "{linkAlias}"."record_id" = @scopeRecordId
                limit 1
                for share of "{linkAlias}", "{linkedAlias}"
                """
            : $"""
                select 1
                from _runtime.runtime_user_links as "{linkAlias}"
                join "app".{QuoteRecordScopeIdentifier(linkedEntity.TableName)} as "{linkedAlias}"
                  on "{linkedAlias}"."id" = "{linkAlias}"."record_id"
                 and "{linkedAlias}"."archived_at" is null
                join "app".{QuoteRecordScopeIdentifier(targetEntity!.TableName)} as "scope_target"
                  on "scope_target"."id" = "{linkedAlias}".{QuoteRecordScopeIdentifier(linkedField!.ColumnName)}
                 and "scope_target"."archived_at" is null
                where "{linkAlias}"."runtime_user_id" = @scopeUserId
                  and "{linkAlias}"."entity_key" = @scopeEntityId
                  and (
                    select count(*)
                    from _runtime.runtime_user_links as "scope_link_count"
                    where "scope_link_count"."runtime_user_id" = @scopeUserId
                      and "scope_link_count"."entity_key" = @scopeEntityId
                  ) = 1
                  and "scope_target"."id" = @scopeRecordId
                limit 1
                for share of "{linkAlias}", "{linkedAlias}", "scope_target"
                """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scopeUserId", user.UserId);
        command.Parameters.AddWithValue("scopeEntityId", linkedEntity.Id);
        command.Parameters.AddWithValue("scopeRecordId", governedRecordId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static RuntimeResolvedRecordScopeField? ResolveGovernedRecordScopeField(
        RuntimeEntityDefinition entity,
        RecordScopeDefinition scope)
    {
        if (!IsSafeRecordScopeIdentifier(scope.Field) ||
            !IsSafeRecordScopeIdentifier(scope.Source) ||
            scope.LinkedEntity is not null && !IsSafeRecordScopeIdentifier(scope.LinkedEntity) ||
            scope.LinkedField is not null && !IsSafeRecordScopeIdentifier(scope.LinkedField))
        {
            return null;
        }

        if (scope.Field.Equals("created_by", StringComparison.Ordinal))
        {
            return new RuntimeResolvedRecordScopeField(
                "created_by",
                "created_by",
                FieldType: "integer",
                ReferencesEntity: null,
                ActorPrincipalCompatible: true,
                LinkedRecordCompatible: false);
        }

        var matchingFields = entity.Fields
            .Where(candidate =>
                candidate.EntityId.Equals(entity.Id, StringComparison.Ordinal) &&
                candidate.Id.Equals($"{entity.Id}.{scope.Field}", StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var field = matchingFields.Length == 1 ? matchingFields[0] : null;
        if (field is null || field.Computed is not null || !IsSafeRecordScopeIdentifier(field.ColumnName))
        {
            return null;
        }

        return new RuntimeResolvedRecordScopeField(
            scope.Field,
            field.ColumnName,
            field.FieldType,
            field.ReferencesEntity,
            ActorPrincipalCompatible: field.FieldType.Equals("integer", StringComparison.OrdinalIgnoreCase),
            LinkedRecordCompatible: field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrWhiteSpace(field.ReferencesEntity));
    }

    private static async Task<RuntimeRecordScopeEntity?> LoadRecordScopeEntityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string entityId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeRecordScopeIdentifier(entityId))
        {
            return null;
        }

        string tableName;
        await using (var entityCommand = new NpgsqlCommand(
                         "select table_name from _runtime.entity_definitions where id = @entityId limit 1",
                         connection,
                         transaction))
        {
            entityCommand.Parameters.AddWithValue("entityId", entityId);
            tableName = await entityCommand.ExecuteScalarAsync(cancellationToken) as string ?? string.Empty;
        }

        if (!IsSafeRecordScopeIdentifier(tableName))
        {
            return null;
        }

        var fields = new List<RuntimeRecordScopeField>();
        await using var fieldCommand = new NpgsqlCommand(
            """
            select id, name, field_type, column_name, references_entity, metadata
            from _runtime.field_definitions
            where entity_id = @entityId
            order by id
            """,
            connection,
            transaction);
        fieldCommand.Parameters.AddWithValue("entityId", entityId);
        await using var reader = await fieldCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fieldId = reader.GetString(0);
            var fieldName = reader.GetString(1);
            var fieldType = reader.GetString(2);
            var columnName = reader.GetString(3);
            var qualifiedPrefix = $"{entityId}.";
            var localFieldId = fieldId.StartsWith(qualifiedPrefix, StringComparison.Ordinal)
                ? fieldId[qualifiedPrefix.Length..]
                : string.Empty;
            if (!IsSafeRecordScopeIdentifier(fieldName) ||
                !IsSafeRecordScopeIdentifier(localFieldId) ||
                !IsSafeRecordScopeIdentifier(columnName))
            {
                return null;
            }

            bool computed;
            try
            {
                if (reader.IsDBNull(5))
                {
                    return null;
                }

                using var metadataDocument = JsonDocument.Parse(reader.GetString(5));
                var metadata = metadataDocument.RootElement;
                computed = metadata.TryGetProperty("Computed", out var computedElement) &&
                           computedElement.ValueKind != JsonValueKind.Null ||
                           metadata.TryGetProperty("computed", out computedElement) &&
                           computedElement.ValueKind != JsonValueKind.Null;
            }
            catch (JsonException)
            {
                return null;
            }

            fields.Add(new RuntimeRecordScopeField(
                fieldId,
                localFieldId,
                fieldType,
                columnName,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                computed));
        }

        return new RuntimeRecordScopeEntity(entityId, tableName, fields);
    }

    private static RuntimeRecordScopeField? FindSingleRecordScopeField(
        IReadOnlyList<RuntimeRecordScopeField> fields,
        string fieldId)
    {
        var matching = fields
            .Where(field => field.Id.Equals(fieldId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }

    private static bool TryReadCandidateValue(
        IReadOnlyDictionary<string, object?> candidate,
        RuntimeResolvedRecordScopeField field,
        out object? value)
    {
        foreach (var key in new[] { field.ColumnName, field.Id })
        {
            if (candidate.TryGetValue(key, out value))
            {
                return true;
            }

            var match = candidate.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null)
            {
                value = match.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static long? ToInt64(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            long typed => typed,
            int typed => typed,
            short typed => typed,
            byte typed => typed,
            _ when long.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static bool IsSafeRecordScopeIdentifier(string? value) =>
        value is not null && RecordScopeIdentifierPattern.IsMatch(value);

    private static string QuoteRecordScopeIdentifier(string value)
    {
        if (!IsSafeRecordScopeIdentifier(value))
        {
            throw new InvalidOperationException("Record-scope metadata contained an unsafe identifier.");
        }

        return $"\"{value}\"";
    }

    private sealed record RuntimeResolvedRecordScopeField(
        string Id,
        string ColumnName,
        string FieldType,
        string? ReferencesEntity,
        bool ActorPrincipalCompatible,
        bool LinkedRecordCompatible);

    private sealed record RuntimeRecordScopeEntity(
        string Id,
        string TableName,
        IReadOnlyList<RuntimeRecordScopeField> Fields);

    private sealed record RuntimeRecordScopeField(
        string QualifiedId,
        string Id,
        string FieldType,
        string ColumnName,
        string? ReferencesEntity,
        bool Computed);
}

internal sealed record RuntimeRecordAccessPredicate(
    string Sql,
    IReadOnlyList<RuntimeRecordAccessParameter> Parameters,
    bool AllowsAllRows,
    bool DeniesAllRows)
{
    public static RuntimeRecordAccessPredicate AllowAll { get; } = new("true", [], true, false);

    public static RuntimeRecordAccessPredicate DenyAll { get; } = new("false", [], false, true);

    public void AddParameters(NpgsqlCommand command)
    {
        foreach (var parameter in Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
    }
}

internal sealed record RuntimeRecordAccessParameter(string Name, object? Value);
