using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimePermissionService
{
    private static readonly IReadOnlySet<string> EntityPermissionActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "*",
            "read",
            "create",
            "update",
            "effect_create",
            "effect_update",
            "archive",
            "restore",
            "admin",
            "start_workflow",
            "approve_task",
            "reject_task",
            "read_task"
        };

    private async Task<IReadOnlyList<RuntimePermissionRoleDocument>> LoadRoleGrantDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<string> roleIds,
        CancellationToken cancellationToken)
    {
        var documents = new List<RuntimePermissionRoleDocument>();
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        command.CommandText = transaction is null
            ? """
              select id, grants::text
              from _runtime.role_definitions
              where lower(id) = any(@roles)
              order by lower(id), id
              """
            : """
              select id, grants::text
              from _runtime.role_definitions
              where lower(id) = any(@roles)
              order by lower(id), id
              for share
              """;
        command.Parameters.AddWithValue(
            "roles",
            roleIds.Select(role => role.Trim().ToLowerInvariant()).ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new RuntimePermissionRoleDocument(
                reader.GetString(0),
                reader.GetString(1)));
        }

        return documents;
    }

    private static async Task<RuntimePermissionCatalog> LoadPermissionCatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<RuntimePermissionCatalogField>();
        var actions = new Dictionary<string, string>(StringComparer.Ordinal);
        var reportIds = new HashSet<string>(StringComparer.Ordinal);

        await using var command = new NpgsqlCommand(
            """
            select 'entity'::text as kind,
                   id::text as value_1,
                   null::text as value_2,
                   null::text as value_3,
                   null::text as value_4,
                   null::text as value_5,
                   null::text as value_6,
                   null::text as value_7
            from _runtime.entity_definitions
            union all
            select 'field',
                   id,
                   entity_id,
                   name,
                   column_name,
                   field_type,
                   references_entity,
                   metadata::text
            from _runtime.field_definitions
            union all
            select 'action',
                   id,
                   entity_id,
                   null::text,
                   null::text,
                   null::text,
                   null::text,
                   null::text
            from _runtime.action_definitions
            union all
            select 'report',
                   id,
                   null::text,
                   null::text,
                   null::text,
                   null::text,
                   null::text,
                   null::text
            from _runtime.report_definitions
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = reader.GetString(0);
            var value1 = reader.GetString(1);
            switch (kind)
            {
                case "entity":
                    entityIds.Add(value1);
                    break;
                case "field":
                {
                    var entityId = reader.GetString(2);
                    var prefix = $"{entityId}.";
                    var localId = value1.StartsWith(prefix, StringComparison.Ordinal)
                        ? value1[prefix.Length..]
                        : string.Empty;
                    fields.Add(new RuntimePermissionCatalogField(
                        entityId,
                        localId,
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        TryReadComputedFlag(reader.GetString(7))));
                    break;
                }
                case "action":
                    if (!actions.TryAdd(value1, reader.GetString(2)))
                    {
                        actions[value1] = string.Empty;
                    }

                    break;
                case "report":
                    reportIds.Add(value1);
                    break;
            }
        }

        var entities = entityIds.ToDictionary(
            id => id,
            id => new RuntimePermissionCatalogEntity(
                id,
                fields.Where(field => field.EntityId.Equals(id, StringComparison.Ordinal)).ToArray()),
            StringComparer.Ordinal);
        return new RuntimePermissionCatalog(entities, actions, reportIds);
    }

    private static bool TryParseRoleGrants(
        string grantsJson,
        RuntimePermissionCatalog catalog,
        out IReadOnlyList<RuntimePermissionGrant> roleGrants)
    {
        roleGrants = [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(grantsJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<RuntimePermissionGrant>();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryReadGrant(
                        element,
                        out var action,
                        out var entity,
                        out var scope,
                        out var target,
                        out var hasRecordScope,
                        out var recordScopeElement) ||
                    !TryValidateGrant(
                        action,
                        entity,
                        scope,
                        target,
                        hasRecordScope,
                        recordScopeElement,
                        catalog,
                        out var grant))
                {
                    return false;
                }

                if (!identities.Add(GrantIdentity(grant!)))
                {
                    return false;
                }

                parsed.Add(grant!);
            }

            roleGrants = parsed;
            return true;
        }
    }

    private static bool TryValidateGrant(
        string action,
        string entity,
        string scope,
        string target,
        bool hasRecordScope,
        JsonElement recordScopeElement,
        RuntimePermissionCatalog catalog,
        out RuntimePermissionGrant? grant)
    {
        grant = null;
        var normalizedAction = action.Trim().ToLowerInvariant();
        var normalizedScope = scope.Trim().ToLowerInvariant();
        var normalizedEntity = entity.Trim();
        var normalizedTarget = target.Trim();
        if (!PermissionGrantScopes.Supported.Contains(normalizedScope) ||
            normalizedScope != PermissionGrantScopes.Record && hasRecordScope)
        {
            return false;
        }

        switch (normalizedScope)
        {
            case PermissionGrantScopes.Entity:
                if (!EntityPermissionActions.Contains(normalizedAction) ||
                    !string.IsNullOrEmpty(normalizedTarget) ||
                    normalizedEntity == "*" &&
                    normalizedAction is ActionPermissionEffectAnalyzer.EffectCreate or
                        ActionPermissionEffectAnalyzer.EffectUpdate ||
                    normalizedEntity != "*" && !catalog.Entities.ContainsKey(normalizedEntity))
                {
                    return false;
                }

                grant = new RuntimePermissionGrant(normalizedAction, normalizedEntity);
                return true;

            case PermissionGrantScopes.Field:
                if (!GranularPermissionActions.Field.Contains(normalizedAction) ||
                    string.IsNullOrEmpty(normalizedTarget) ||
                    !catalog.Entities.TryGetValue(normalizedEntity, out var fieldEntity) ||
                    FindField(fieldEntity, normalizedTarget, acceptAliases: true) is not { } field)
                {
                    return false;
                }

                grant = new RuntimePermissionGrant(
                    normalizedAction,
                    fieldEntity.Id,
                    PermissionGrantScopes.Field,
                    field.Name);
                return true;

            case PermissionGrantScopes.Page:
                if (!GranularPermissionActions.Page.Contains(normalizedAction) ||
                    string.IsNullOrEmpty(normalizedTarget) ||
                    !RuntimePagePermissionKinds.Supported.Contains(normalizedTarget) ||
                    !IsValidPageResource(normalizedTarget, normalizedEntity, catalog))
                {
                    return false;
                }

                grant = new RuntimePermissionGrant(
                    normalizedAction,
                    normalizedEntity,
                    PermissionGrantScopes.Page,
                    normalizedTarget.ToLowerInvariant());
                return true;

            case PermissionGrantScopes.Action:
                if (!GranularPermissionActions.Action.Contains(normalizedAction) ||
                    string.IsNullOrEmpty(normalizedTarget) ||
                    !catalog.Entities.ContainsKey(normalizedEntity) ||
                    !catalog.Actions.TryGetValue(normalizedTarget, out var actionEntity) ||
                    !actionEntity.Equals(normalizedEntity, StringComparison.Ordinal))
                {
                    return false;
                }

                grant = new RuntimePermissionGrant(
                    normalizedAction,
                    normalizedEntity,
                    PermissionGrantScopes.Action,
                    normalizedTarget);
                return true;

            case PermissionGrantScopes.Record:
                if (!RecordPermissionActions.Supported.Contains(normalizedAction) ||
                    !string.IsNullOrEmpty(normalizedTarget) ||
                    !hasRecordScope ||
                    !catalog.Entities.TryGetValue(normalizedEntity, out var recordEntity) ||
                    !TryReadRecordScope(recordScopeElement, out var recordScope) ||
                    !IsValidRecordScope(recordEntity, recordScope!, catalog))
                {
                    return false;
                }

                grant = new RuntimePermissionGrant(
                    normalizedAction,
                    recordEntity.Id,
                    PermissionGrantScopes.Record,
                    string.Empty,
                    recordScope);
                return true;

            default:
                return false;
        }
    }

    private static bool IsValidPageResource(
        string target,
        string resource,
        RuntimePermissionCatalog catalog)
    {
        var normalizedTarget = target.ToLowerInvariant();
        return normalizedTarget switch
        {
            "dashboard" or "reports" => resource == "*",
            "entity_list" or "entity_create" or "entity_detail" =>
                resource == "*" || catalog.Entities.ContainsKey(resource),
            "report_detail" => resource == "*" || catalog.ReportIds.Contains(resource),
            _ => false
        };
    }

    private static bool IsValidRecordScope(
        RuntimePermissionCatalogEntity entity,
        RecordScopeDefinition scope,
        RuntimePermissionCatalog catalog)
    {
        if (scope.Source.Equals(RecordPermissionScopeSources.ActorPrincipal, StringComparison.Ordinal))
        {
            if (scope.LinkedEntity is not null || scope.LinkedField is not null)
            {
                return false;
            }

            if (scope.Field.Equals("created_by", StringComparison.Ordinal))
            {
                return true;
            }

            var actorField = FindField(entity, scope.Field, acceptAliases: false);
            return actorField is { Computed: false } &&
                   actorField.FieldType.Equals("integer", StringComparison.OrdinalIgnoreCase);
        }

        if (!scope.Source.Equals(RecordPermissionScopeSources.LinkedRecord, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(scope.LinkedEntity) ||
            !catalog.Entities.TryGetValue(scope.LinkedEntity, out var linkedEntity))
        {
            return false;
        }

        var governedField = FindField(entity, scope.Field, acceptAliases: false);
        if (governedField is not { Computed: false } ||
            !governedField.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(governedField.ReferencesEntity) ||
            !catalog.Entities.ContainsKey(governedField.ReferencesEntity))
        {
            return false;
        }

        if (scope.LinkedField is null)
        {
            return governedField.ReferencesEntity.Equals(linkedEntity.Id, StringComparison.Ordinal);
        }

        var linkedField = FindField(linkedEntity, scope.LinkedField, acceptAliases: false);
        return linkedField is { Computed: false } &&
               linkedField.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(linkedField.ReferencesEntity) &&
               catalog.Entities.ContainsKey(linkedField.ReferencesEntity) &&
               linkedField.ReferencesEntity.Equals(governedField.ReferencesEntity, StringComparison.Ordinal);
    }

    private static RuntimePermissionCatalogField? FindField(
        RuntimePermissionCatalogEntity entity,
        string reference,
        bool acceptAliases)
    {
        var matches = entity.Fields
            .Where(field => field.LocalId.Equals(reference, StringComparison.Ordinal) ||
                            acceptAliases &&
                            (field.Name.Equals(reference, StringComparison.OrdinalIgnoreCase) ||
                             field.ColumnName.Equals(reference, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool? TryReadComputedFlag(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var properties = document.RootElement.EnumerateObject()
                .Where(property => property.Name is "Computed" or "computed")
                .Take(2)
                .ToArray();
            if (properties.Length > 1)
            {
                return null;
            }

            if (properties.Length == 0 || properties[0].Value.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            return properties[0].Value.ValueKind == JsonValueKind.Object ? true : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GrantIdentity(RuntimePermissionGrant grant) =>
        string.Join(
            ':',
            grant.Scope.Trim().ToLowerInvariant(),
            grant.Action.Trim().ToLowerInvariant(),
            grant.Entity.Trim().ToLowerInvariant(),
            grant.Target.Trim().ToLowerInvariant(),
            grant.RecordScope?.Field.Trim().ToLowerInvariant() ?? string.Empty,
            grant.RecordScope?.Source.Trim().ToLowerInvariant() ?? string.Empty,
            grant.RecordScope?.LinkedEntity?.Trim().ToLowerInvariant() ?? string.Empty,
            grant.RecordScope?.LinkedField?.Trim().ToLowerInvariant() ?? string.Empty);
}

internal sealed record RuntimePermissionRoleDocument(string RoleId, string GrantsJson);

internal sealed record RuntimePermissionCatalog(
    IReadOnlyDictionary<string, RuntimePermissionCatalogEntity> Entities,
    IReadOnlyDictionary<string, string> Actions,
    IReadOnlySet<string> ReportIds);

internal sealed record RuntimePermissionCatalogEntity(
    string Id,
    IReadOnlyList<RuntimePermissionCatalogField> Fields);

internal sealed record RuntimePermissionCatalogField(
    string EntityId,
    string LocalId,
    string Name,
    string ColumnName,
    string FieldType,
    string? ReferencesEntity,
    bool? Computed);
