using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimePermissionService(ITenantDatabaseConnectionFactory connectionFactory)
    : IRuntimePermissionService
{
    private static readonly IReadOnlySet<string> RuntimeSystemRecordFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "created_at",
        "created_by",
        "updated_at",
        "updated_by",
        "archived_at",
        "archived_by",
        "archive_reason"
    };

    public async Task<bool> IsAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string action,
        RuntimeEntityDefinition? entity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        var snapshot = await CreateSnapshotAsync(connection, user, cancellationToken);
        return IsAllowed(snapshot, action, entity);
    }

    internal Task<RuntimePermissionSnapshot> CreateSnapshotAsync(
        NpgsqlConnection connection,
        RuntimeUserContext user,
        CancellationToken cancellationToken = default) =>
        CreateSnapshotAsync(connection, transaction: null, user, cancellationToken);

    internal async Task<RuntimePermissionSnapshot> CreateSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimeUserContext user,
        CancellationToken cancellationToken = default)
    {
        var userRoles = await LoadRoleIdsAsync(connection, transaction, user, cancellationToken);
        if (userRoles.Count == 0)
        {
            return RuntimePermissionSnapshot.Empty;
        }

        if (userRoles.Any(RuntimeSystemRoleIds.IsReserved))
        {
            return new RuntimePermissionSnapshot(true, [], userRoles);
        }

        var roleDocuments = await LoadRoleGrantDocumentsAsync(
            connection,
            transaction,
            userRoles,
            cancellationToken);
        var catalog = await LoadPermissionCatalogAsync(connection, transaction, cancellationToken);
        var grants = new List<RuntimePermissionGrant>();
        foreach (var roleGroup in roleDocuments.GroupBy(
                     document => document.RoleId,
                     StringComparer.OrdinalIgnoreCase))
        {
            // The database primary key is case-sensitive. Ambiguous role ids
            // that differ only by case must not combine into broader authority.
            if (roleGroup.Take(2).Count() != 1)
            {
                continue;
            }

            var roleDocument = roleGroup.Single();
            if (TryParseRoleGrants(roleDocument.GrantsJson, catalog, out var roleGrants))
            {
                grants.AddRange(roleGrants);
            }
        }

        return new RuntimePermissionSnapshot(false, grants, userRoles);
    }

    internal bool IsAllowed(
        RuntimePermissionSnapshot snapshot,
        string action,
        RuntimeEntityDefinition? entity)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        if (snapshot.HasSystemOwnerAuthority)
        {
            return true;
        }

        var normalizedAction = action.Trim().ToLowerInvariant();
        if (normalizedAction is ActionPermissionEffectAnalyzer.EffectCreate or
            ActionPermissionEffectAnalyzer.EffectUpdate)
        {
            return entity is not null && snapshot.Grants.Any(grant =>
                grant.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase) &&
                grant.Action.Equals(normalizedAction, StringComparison.OrdinalIgnoreCase) &&
                (grant.Entity.Equals(entity.Id, StringComparison.OrdinalIgnoreCase) ||
                 grant.Entity.Equals(entity.Key, StringComparison.OrdinalIgnoreCase)));
        }

        return snapshot.Grants.Any(grant =>
            (grant.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase) ||
             grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.OrdinalIgnoreCase)) &&
            ActionMatches(grant.Action, normalizedAction) &&
            EntityMatches(grant.Entity, entity));
    }

    public async Task<bool> IsFieldAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string action,
        RuntimeEntityDefinition entity,
        string field,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        var snapshot = await CreateSnapshotAsync(connection, user, cancellationToken);
        return IsFieldAllowed(snapshot, action, entity, field);
    }

    public async Task<bool> AreFieldsAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string action,
        RuntimeEntityDefinition entity,
        IEnumerable<string> fields,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        var snapshot = await CreateSnapshotAsync(connection, user, cancellationToken);
        return fields.All(field => IsFieldAllowed(snapshot, action, entity, field));
    }

    public async Task<bool> IsPageAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string page,
        string resource,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        var snapshot = await CreateSnapshotAsync(connection, user, cancellationToken);
        return IsPageAllowed(snapshot, page, resource);
    }

    public async Task<bool> IsNamedActionAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        string actionKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        var snapshot = await CreateSnapshotAsync(connection, user, cancellationToken);
        return IsNamedActionAllowed(snapshot, entity, actionKey);
    }

    public async Task<RuntimeEntityDefinition> FilterReadableEntityAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        var snapshot = await CreateSnapshotAsync(connection, user, cancellationToken);
        return FilterReadableEntity(snapshot, entity);
    }

    /// <summary>
    /// Removes unreadable fields from an already row-authorized list. This
    /// method never authorizes or filters record rows; callers must obtain the
    /// list through a user-scoped RuntimeRecordStore API first.
    /// </summary>
    public async Task<RuntimeRecordList> FilterReadableRecordListFieldsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeRecordList list,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        var snapshot = await CreateSnapshotAsync(connection, user, cancellationToken);
        var sourceEntity = list.Entity;
        var entity = FilterReadableEntity(snapshot, sourceEntity);
        return list with
        {
            Entity = entity,
            Records = list.Records.Select(record => FilterReadableRecord(sourceEntity, entity, record)).ToArray()
        };
    }

    /// <summary>
    /// Removes unreadable fields from an already row-authorized record. This
    /// method is not a row-authorization check.
    /// </summary>
    public async Task<Dictionary<string, object?>> FilterReadableRecordFieldsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeEntityDefinition entity,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        var snapshot = await CreateSnapshotAsync(connection, user, cancellationToken);
        return FilterReadableRecord(entity, FilterReadableEntity(snapshot, entity), record);
    }

    internal bool IsFieldAllowed(
        RuntimePermissionSnapshot snapshot,
        string action,
        RuntimeEntityDefinition entity,
        string field)
    {
        if (snapshot.HasSystemOwnerAuthority)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(field))
        {
            return false;
        }

        var grants = snapshot.Grants.Where(grant =>
            grant.Scope.Equals(PermissionGrantScopes.Field, StringComparison.OrdinalIgnoreCase) &&
            ActionMatches(grant.Action, action.Trim().ToLowerInvariant()) &&
            EntityMatches(grant.Entity, entity)).ToArray();
        var requiresExplicitEffectField = action.Trim().Equals(
            GranularPermissionActions.EffectWrite,
            StringComparison.OrdinalIgnoreCase);
        var requestedField = field.Trim();
        return (!requiresExplicitEffectField && grants.Length == 0) || grants.Any(grant =>
            grant.Target.Equals(requestedField, StringComparison.OrdinalIgnoreCase) ||
            entity.Fields.Any(candidate =>
                FieldMatchesReference(candidate, grant.Target) &&
                FieldMatchesReference(candidate, requestedField)));
    }

    internal static bool FieldMatchesReference(RuntimeFieldDefinition field, string reference)
    {
        if (field.Id.Equals(reference, StringComparison.OrdinalIgnoreCase) ||
            field.Name.Equals(reference, StringComparison.OrdinalIgnoreCase) ||
            field.ColumnName.Equals(reference, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var qualifiedIdPrefix = $"{field.EntityId}.";
        return field.Id.StartsWith(qualifiedIdPrefix, StringComparison.OrdinalIgnoreCase) &&
               field.Id[qualifiedIdPrefix.Length..].Equals(reference, StringComparison.OrdinalIgnoreCase);
    }

    internal RuntimeEntityDefinition FilterReadableEntity(
        RuntimePermissionSnapshot snapshot,
        RuntimeEntityDefinition entity)
    {
        var readableFields = entity.Fields
            .Where(field => IsFieldAllowed(snapshot, GranularPermissionActions.Read, entity, field.Name))
            .ToArray();
        return WithReadableFields(entity, readableFields);
    }

    internal RuntimeEntityDefinition FilterExplicitTaskReadableEntity(
        RuntimePermissionSnapshot snapshot,
        RuntimeEntityDefinition entity)
    {
        if (snapshot.HasSystemOwnerAuthority)
        {
            return entity;
        }

        var explicitReadGrants = snapshot.Grants.Where(grant =>
            grant.Scope.Equals(PermissionGrantScopes.Field, StringComparison.OrdinalIgnoreCase) &&
            ActionMatches(grant.Action, GranularPermissionActions.Read) &&
            EntityMatches(grant.Entity, entity)).ToArray();
        var readableFields = entity.Fields.Where(field => explicitReadGrants.Any(grant =>
            FieldMatchesReference(field, grant.Target))).ToArray();
        return WithReadableFields(entity, readableFields);
    }

    private static RuntimeEntityDefinition WithReadableFields(
        RuntimeEntityDefinition entity,
        IReadOnlyList<RuntimeFieldDefinition> readableFields)
    {
        var readableIdentifiers = readableFields
            .SelectMany(field => new[] { field.Id, field.Name, field.ColumnName })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var primary = entity.DisplayPrimaryField.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                      readableIdentifiers.Contains(entity.DisplayPrimaryField)
            ? entity.DisplayPrimaryField
            : "id";
        var secondary = !string.IsNullOrWhiteSpace(entity.DisplaySecondaryField) &&
                        (entity.DisplaySecondaryField.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                         readableIdentifiers.Contains(entity.DisplaySecondaryField))
            ? entity.DisplaySecondaryField
            : null;

        return entity with
        {
            DisplayPrimaryField = primary,
            DisplaySecondaryField = secondary,
            Fields = readableFields
        };
    }

    internal static Dictionary<string, object?> FilterReadableRecord(
        RuntimeEntityDefinition sourceEntity,
        RuntimeEntityDefinition filteredEntity,
        IReadOnlyDictionary<string, object?> record)
    {
        var readable = filteredEntity.Fields
            .SelectMany(field => new[] { field.Id, field.Name, field.ColumnName })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declared = sourceEntity.Fields
            .SelectMany(field => new[] { field.Id, field.Name, field.ColumnName })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readableRelations = filteredEntity.Fields
            .Where(field => field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase))
            .SelectMany(field => new[] { field.Id, field.Name, field.ColumnName })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in record)
        {
            if (RuntimeSystemRecordFields.Contains(key) || readable.Contains(key))
            {
                result[key] = value;
                continue;
            }

            if (declared.Contains(key))
            {
                continue;
            }

            var relationKey = key.EndsWith("__secondaryLabel", StringComparison.OrdinalIgnoreCase)
                ? key[..^16]
                : key.EndsWith("__label", StringComparison.OrdinalIgnoreCase)
                    ? key[..^7]
                    : null;
            if (relationKey is not null && readableRelations.Contains(relationKey))
            {
                result[key] = value;
            }
        }

        return result;
    }

    internal bool IsPageAllowed(
        RuntimePermissionSnapshot snapshot,
        string page,
        string resource)
    {
        if (snapshot.HasSystemOwnerAuthority)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(page) || string.IsNullOrWhiteSpace(resource))
        {
            return false;
        }

        var grants = snapshot.Grants
            .Where(grant => grant.Scope.Equals(PermissionGrantScopes.Page, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return grants.Length == 0 || grants.Any(grant =>
            ActionMatches(grant.Action, GranularPermissionActions.View) &&
            grant.Target.Equals(page.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (grant.Entity.Equals("*", StringComparison.Ordinal) ||
             grant.Entity.Equals(resource.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    internal bool IsNamedActionAllowed(
        RuntimePermissionSnapshot snapshot,
        RuntimeEntityDefinition entity,
        string actionKey)
    {
        if (snapshot.HasSystemOwnerAuthority)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actionKey))
        {
            return false;
        }

        var grants = snapshot.Grants
            .Where(grant => grant.Scope.Equals(PermissionGrantScopes.Action, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return grants.Any(grant =>
            ActionMatches(grant.Action, GranularPermissionActions.Execute) &&
            EntityMatches(grant.Entity, entity) &&
            grant.Target.Equals(actionKey.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<string>> LoadRoleIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimeUserContext user,
        CancellationToken cancellationToken)
    {
        var roles = new List<string>();
        var persistedUserExists = false;
        if (transaction is null)
        {
            await using var command = new NpgsqlCommand { Connection = connection };
            command.CommandText = """
                select u.status, r.role_id
                from _runtime.runtime_users u
                left join _runtime.runtime_user_roles r on r.user_id = u.id
                where u.id = @userId
                """;
            command.Parameters.AddWithValue("userId", user.UserId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                persistedUserExists = true;
                if (reader.GetString(0).Equals("active", StringComparison.OrdinalIgnoreCase) &&
                    !reader.IsDBNull(1))
                {
                    roles.Add(reader.GetString(1));
                }
            }
        }
        else
        {
            string? status;
            await using (var userCommand = new NpgsqlCommand(
                             "select status from _runtime.runtime_users where id = @userId for share",
                             connection,
                             transaction))
            {
                userCommand.Parameters.AddWithValue("userId", user.UserId);
                status = await userCommand.ExecuteScalarAsync(cancellationToken) as string;
            }

            persistedUserExists = status is not null;
            if (persistedUserExists)
            {
                await using var roleCommand = new NpgsqlCommand(
                    "select role_id from _runtime.runtime_user_roles where user_id = @userId order by role_id for share",
                    connection,
                    transaction);
                roleCommand.Parameters.AddWithValue("userId", user.UserId);
                await using var reader = await roleCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (status!.Equals("active", StringComparison.OrdinalIgnoreCase))
                    {
                        roles.Add(reader.GetString(0));
                    }
                }
            }
        }

        // Synthetic acceptance-test actors do not have runtime_users rows and
        // intentionally carry their generated role in the caller context. A
        // persisted user with no durable role rows, however, is revoked and
        // must never regain stale session/context roles through this fallback.
        if (!persistedUserExists)
        {
            roles.AddRange(user.Roles);
        }

        return roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ActionMatches(string? grantAction, string action) =>
        grantAction is "*" ||
        string.Equals(grantAction, action, StringComparison.OrdinalIgnoreCase);

    private static bool EntityMatches(string? grantEntity, RuntimeEntityDefinition? entity)
    {
        if (grantEntity is "*")
        {
            return true;
        }

        if (entity is null || string.IsNullOrWhiteSpace(grantEntity))
        {
            return false;
        }

        return string.Equals(grantEntity, entity.Id, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(grantEntity, entity.Name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(grantEntity, entity.Key, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(grantEntity, entity.TableName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadGrant(
        JsonElement element,
        out string action,
        out string entity,
        out string scope,
        out string target,
        out bool hasRecordScope,
        out JsonElement recordScope)
    {
        action = string.Empty;
        entity = string.Empty;
        scope = PermissionGrantScopes.Entity;
        target = string.Empty;
        hasRecordScope = false;
        recordScope = default;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var canonicalName = property.Name switch
            {
                "Action" or "action" => "action",
                "Entity" or "entity" => "entity",
                "Scope" or "scope" => "scope",
                "Target" or "target" => "target",
                "RecordScope" or "recordScope" => "recordScope",
                _ => null
            };
            if (canonicalName is null || !seen.Add(canonicalName))
            {
                return false;
            }

            if (canonicalName == "recordScope")
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                hasRecordScope = true;
                recordScope = property.Value;
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = property.Value.GetString() ?? string.Empty;
            switch (canonicalName)
            {
                case "action":
                    action = value;
                    break;
                case "entity":
                    entity = value;
                    break;
                case "scope":
                    scope = value;
                    break;
                case "target":
                    target = value;
                    break;
            }
        }

        return seen.Contains("action") &&
               seen.Contains("entity") &&
               !string.IsNullOrWhiteSpace(action) &&
               !string.IsNullOrWhiteSpace(entity) &&
               !string.IsNullOrWhiteSpace(scope);
    }

    private static bool TryReadRecordScope(
        JsonElement element,
        out RecordScopeDefinition? recordScope)
    {
        recordScope = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? field = null;
        string? source = null;
        string? linkedEntity = null;
        string? linkedField = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var canonicalName = property.Name switch
            {
                "Field" or "field" => "field",
                "Source" or "source" => "source",
                "LinkedEntity" or "linkedEntity" => "linkedEntity",
                "LinkedField" or "linkedField" => "linkedField",
                _ => null
            };
            if (canonicalName is null || !seen.Add(canonicalName))
            {
                return false;
            }

            if (property.Value.ValueKind == JsonValueKind.Null &&
                canonicalName is "linkedEntity" or "linkedField")
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = property.Value.GetString();
            switch (canonicalName)
            {
                case "field":
                    field = value;
                    break;
                case "source":
                    source = value;
                    break;
                case "linkedEntity":
                    linkedEntity = value;
                    break;
                case "linkedField":
                    linkedField = value;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(field) ||
            string.IsNullOrWhiteSpace(source) ||
            !IsSafeRecordScopeIdentifier(field.Trim()) ||
            !IsSafeRecordScopeIdentifier(source.Trim()) ||
            !RecordPermissionScopeSources.Supported.Contains(source.Trim()))
        {
            return false;
        }

        var normalizedSource = source.Trim().ToLowerInvariant();
        linkedEntity = string.IsNullOrWhiteSpace(linkedEntity) ? null : linkedEntity.Trim();
        linkedField = string.IsNullOrWhiteSpace(linkedField) ? null : linkedField.Trim();
        if (linkedEntity is not null && !IsSafeRecordScopeIdentifier(linkedEntity) ||
            linkedField is not null && !IsSafeRecordScopeIdentifier(linkedField))
        {
            return false;
        }
        if (normalizedSource == RecordPermissionScopeSources.ActorPrincipal &&
            (linkedEntity is not null || linkedField is not null))
        {
            return false;
        }

        if (normalizedSource == RecordPermissionScopeSources.LinkedRecord && linkedEntity is null)
        {
            return false;
        }

        recordScope = new RecordScopeDefinition(
            field.Trim(),
            normalizedSource,
            linkedEntity,
            linkedField);
        return true;
    }
}

internal sealed record RuntimePermissionSnapshot(
    bool HasSystemOwnerAuthority,
    IReadOnlyList<RuntimePermissionGrant> Grants,
    IReadOnlyList<string>? RoleIds = null)
{
    public static RuntimePermissionSnapshot Empty { get; } = new(false, [], []);

    public IReadOnlyList<string> EffectiveRoleIds => RoleIds ?? [];
}

internal sealed record RuntimePermissionGrant(
    string Action,
    string Entity,
    string Scope = PermissionGrantScopes.Entity,
    string Target = "",
    RecordScopeDefinition? RecordScope = null);
