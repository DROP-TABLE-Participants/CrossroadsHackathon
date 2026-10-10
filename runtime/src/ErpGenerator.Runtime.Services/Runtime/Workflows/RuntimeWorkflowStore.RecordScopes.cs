using Npgsql;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeWorkflowStore
{
    private async Task<RuntimeRecordAccessPredicate> BuildDirectAssignedTaskVisibilityPredicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimePermissionSnapshot snapshot,
        string rowAlias,
        string parameterPrefix,
        CancellationToken cancellationToken)
    {
        if (!IdentifierPattern.IsMatch(rowAlias) || !IdentifierPattern.IsMatch(parameterPrefix))
        {
            return RuntimeRecordAccessPredicate.DenyAll;
        }

        var entityIds = new List<string>();
        await using (var command = new NpgsqlCommand(
                         "select id from _runtime.entity_definitions order by id",
                         connection,
                         transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                entityIds.Add(reader.GetString(0));
            }
        }

        var clauses = new List<string>();
        var parameters = new List<RuntimeRecordAccessParameter>();
        var quotedRowAlias = Quote(rowAlias);
        for (var index = 0; index < entityIds.Count; index++)
        {
            var entity = await LoadEntityByIdAsync(connection, entityIds[index], cancellationToken);
            if (entity is null || !permissions.IsAllowed(snapshot, "read_task", entity))
            {
                continue;
            }

            var entityParameter = $"{parameterPrefix}_entity_{index}";
            parameters.Add(new RuntimeRecordAccessParameter(entityParameter, entity.Id));
            clauses.Add($"{quotedRowAlias}.\"entity_id\" = @{entityParameter}");
        }

        return clauses.Count == 0
            ? RuntimeRecordAccessPredicate.DenyAll
            : new RuntimeRecordAccessPredicate(
                $"({string.Join(" or ", clauses)})",
                parameters,
                AllowsAllRows: false,
                DeniesAllRows: false);
    }

    private async Task<RuntimeRecordAccessPredicate> BuildWorkflowRecordVisibilityPredicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        string rowAlias,
        string parameterPrefix,
        CancellationToken cancellationToken)
    {
        if (!IdentifierPattern.IsMatch(rowAlias) || !IdentifierPattern.IsMatch(parameterPrefix))
        {
            return RuntimeRecordAccessPredicate.DenyAll;
        }

        var entityIds = new List<string>();
        await using (var command = new NpgsqlCommand(
                         "select id from _runtime.entity_definitions order by id",
                         connection,
                         transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                entityIds.Add(reader.GetString(0));
            }
        }

        var clauses = new List<string>();
        var parameters = new List<RuntimeRecordAccessParameter>();
        var quotedRowAlias = Quote(rowAlias);
        for (var index = 0; index < entityIds.Count; index++)
        {
            var entity = await LoadEntityByIdAsync(connection, entityIds[index], cancellationToken);
            if (entity is null ||
                !permissions.IsAllowed(snapshot, "read_task", entity))
            {
                continue;
            }

            if (!permissions.IsAllowed(snapshot, "read", entity))
            {
                continue;
            }

            var recordAlias = $"wf_scope_{index}";
            var readPredicate = await permissions.BuildRecordPredicateAsync(
                connection,
                transaction,
                snapshot,
                user,
                "read",
                entity,
                recordAlias,
                $"{parameterPrefix}_record_{index}",
                cancellationToken);
            if (readPredicate.DeniesAllRows)
            {
                continue;
            }

            var entityParameter = $"{parameterPrefix}_entity_{index}";
            parameters.Add(new RuntimeRecordAccessParameter(entityParameter, entity.Id));
            parameters.AddRange(readPredicate.Parameters);
            clauses.Add($"""
                ({quotedRowAlias}."entity_id" = @{entityParameter}
                 and exists (
                   select 1
                   from {Quote(entity.SchemaName)}.{Quote(entity.TableName)} as {Quote(recordAlias)}
                   where {Quote(recordAlias)}."id" = {quotedRowAlias}."record_id"
                     and {Quote(recordAlias)}."archived_at" is null
                     and ({readPredicate.Sql})
                 ))
                """);
        }

        return clauses.Count == 0
            ? RuntimeRecordAccessPredicate.DenyAll
            : new RuntimeRecordAccessPredicate(
                $"({string.Join(" or ", clauses)})",
                parameters,
                AllowsAllRows: false,
                DeniesAllRows: false);
    }

    private async Task<bool> CanViewWorkflowRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimePermissionSnapshot snapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken)
    {
        if (!permissions.IsAllowed(snapshot, "read_task", entity))
        {
            return false;
        }

        return permissions.IsAllowed(snapshot, "read", entity) &&
               await permissions.IsCandidateRecordAllowedAsync(
                   connection,
                   transaction,
                   snapshot,
                   user,
                   "read",
                   entity,
                   record,
                   cancellationToken);
    }

    private bool HasWorkflowAdminAuthority(RuntimePermissionSnapshot snapshot) =>
        permissions.IsAllowed(snapshot, "admin", entity: null);
}
