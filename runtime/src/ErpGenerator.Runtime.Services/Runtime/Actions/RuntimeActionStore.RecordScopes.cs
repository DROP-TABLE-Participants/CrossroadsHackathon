using Npgsql;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeActionStore
{
    private async Task<string?> FindDeniedSourceActionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition sourceEntity,
        IReadOnlyDictionary<string, object?> sourceRecord,
        IReadOnlyList<RuntimeActionAuthorizationRequirement> requirements,
        CancellationToken cancellationToken)
    {
        if (!await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                permissionSnapshot,
                user,
                "read",
                sourceEntity,
                sourceRecord,
                cancellationToken))
        {
            return "read";
        }

        foreach (var requirement in requirements.Where(requirement =>
                     requirement.Entity.Id.Equals(sourceEntity.Id, StringComparison.OrdinalIgnoreCase) &&
                     !requirement.Action.Equals("read", StringComparison.OrdinalIgnoreCase)))
        {
            if (!await permissions.IsCandidateRecordAllowedAsync(
                    connection,
                    transaction,
                    permissionSnapshot,
                    user,
                    requirement.Action,
                    sourceEntity,
                    sourceRecord,
                    cancellationToken))
            {
                return requirement.Action;
            }
        }

        return null;
    }

    private async Task EnsureSourceRecordRequirementsAllowedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeUserContext user,
        RuntimeEntityDefinition sourceEntity,
        IReadOnlyDictionary<string, object?> sourceRecord,
        long sourceRecordId,
        IReadOnlyList<RuntimeActionAuthorizationRequirement> requirements,
        CancellationToken cancellationToken)
    {
        var deniedAction = await FindDeniedSourceActionAsync(
            connection,
            transaction,
            permissionSnapshot,
            user,
            sourceEntity,
            sourceRecord,
            requirements,
            cancellationToken);
        if (deniedAction is not null)
        {
            if (deniedAction.Equals("read", StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeActionRecordNotFoundException(sourceEntity.Key, sourceRecordId);
            }

            throw RecordScopePermissionDenied(deniedAction, sourceEntity);
        }
    }

    private async Task EnsureEffectRecordAllowedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimePermissionSnapshot permissionSnapshot,
        RuntimeUserContext user,
        string action,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> candidate,
        CancellationToken cancellationToken)
    {
        if (!await permissions.IsCandidateRecordAllowedAsync(
                connection,
                transaction,
                permissionSnapshot,
                user,
                action,
                entity,
                candidate,
                cancellationToken))
        {
            throw RecordScopePermissionDenied(action, entity);
        }
    }

    private static RuntimeActionPermissionException RecordScopePermissionDenied(
        string action,
        RuntimeEntityDefinition entity) =>
        new(
            action.Trim().ToLowerInvariant(),
            entity.Id,
            "Action record access denied.");
}
