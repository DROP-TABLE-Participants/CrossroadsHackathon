using ErpGenerator.Runtime.Shared.Bundles;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundlePermissionValidator
{
    public static void ValidateRoles(ErpBundle bundle, HashSet<string> entityIds, List<string> errors)
    {
        if (bundle.Roles.Count == 0)
        {
            errors.Add("ERP Bundle must declare at least one explicit role with explicit grants; no default roles or owner grants are inferred.");
            return;
        }

        foreach (var duplicate in bundle.Roles.Keys
                     .Where(roleId => !string.IsNullOrWhiteSpace(roleId))
                     .GroupBy(NormalizeRoleId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"ERP Bundle contains duplicate normalized role id '{duplicate}'.");
        }

        var roles = EffectiveRoles(bundle);
        foreach (var (roleId, role) in roles)
        {
            if (RuntimeSystemRoleIds.IsReserved(roleId))
            {
                errors.Add($"Role id '{roleId}' is reserved for tenant-local runtime bootstrap authority and cannot be declared by an ERP Bundle.");
            }

            if (string.IsNullOrWhiteSpace(roleId) || !RoleIdentifierPattern.IsMatch(roleId))
            {
                errors.Add($"Role id '{roleId}' must start with a letter and contain only letters, numbers, or underscore.");
            }

            RejectSqlLikeContent(roleId, $"role[{roleId}].id", errors);
            RejectSqlLikeContent(role.DisplayName, $"role[{roleId}].displayName", errors);

            if (role.Grants.Count == 0)
            {
                errors.Add($"Role '{roleId}' must contain at least one grant.");
            }

            foreach (var grant in role.Grants)
            {
                var scope = string.IsNullOrWhiteSpace(grant.Scope)
                    ? PermissionGrantScopes.Entity
                    : grant.Scope.Trim().ToLowerInvariant();
                if (grant.RecordScope is not null &&
                    !scope.Equals(PermissionGrantScopes.Record, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Role '{roleId}' {scope} grant must not declare recordScope; recordScope is valid only for scope 'record'.");
                }

                if (!PermissionGrantScopes.Supported.Contains(scope))
                {
                    errors.Add($"Role '{roleId}' grant scope '{grant.Scope}' is not supported.");
                    continue;
                }

                switch (scope)
                {
                    case PermissionGrantScopes.Entity:
                        ValidateEntityGrant(roleId, grant, entityIds, errors);
                        break;
                    case PermissionGrantScopes.Field:
                        ValidateFieldGrant(bundle, roleId, grant, entityIds, errors);
                        break;
                    case PermissionGrantScopes.Page:
                        ValidatePageGrant(bundle, roleId, grant, entityIds, errors);
                        break;
                    case PermissionGrantScopes.Action:
                        ValidateActionGrant(bundle, roleId, grant, entityIds, errors);
                        break;
                    case PermissionGrantScopes.Record:
                        ValidateRecordGrant(bundle, roleId, grant, entityIds, errors);
                        break;
                }

                RejectSqlLikeContent(grant.Entity, $"role[{roleId}].grant.entity", errors);
                RejectSqlLikeContent(grant.Target, $"role[{roleId}].grant.target", errors);
                if (grant.RecordScope is not null)
                {
                    RejectSqlLikeContent(grant.RecordScope.Field, $"role[{roleId}].grant.recordScope.field", errors);
                    RejectSqlLikeContent(grant.RecordScope.Source, $"role[{roleId}].grant.recordScope.source", errors);
                    RejectSqlLikeContent(grant.RecordScope.LinkedEntity, $"role[{roleId}].grant.recordScope.linkedEntity", errors);
                    RejectSqlLikeContent(grant.RecordScope.LinkedField, $"role[{roleId}].grant.recordScope.linkedField", errors);
                }
            }

            foreach (var duplicate in role.Grants
                         .Select(GrantIdentity)
                         .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1)
                         .Select(group => group.Key))
            {
                errors.Add($"Role '{roleId}' contains duplicate permission grant '{duplicate}'.");
            }
        }
    }

    private static void ValidateEntityGrant(
        string roleId,
        ErpGrant grant,
        IReadOnlySet<string> entityIds,
        List<string> errors)
    {
        if (!PermissionActions.Contains(grant.Action))
        {
            errors.Add($"Role '{roleId}' entity grant action '{grant.Action}' is not supported.");
        }

        ValidateEntityReference(roleId, grant, entityIds, allowWildcard: true, errors);
        if (grant.Entity == "*" &&
            grant.Action.Trim().ToLowerInvariant() is
                ActionPermissionEffectAnalyzer.EffectCreate or
                ActionPermissionEffectAnalyzer.EffectUpdate)
        {
            errors.Add(
                $"Role '{roleId}' effect grant '{grant.Action}' must target one exact entity; wildcard effect authority is forbidden.");
        }

        if (!string.IsNullOrWhiteSpace(grant.Target))
        {
            errors.Add($"Role '{roleId}' entity grant must not declare target '{grant.Target}'.");
        }
    }

    private static void ValidateFieldGrant(
        ErpBundle bundle,
        string roleId,
        ErpGrant grant,
        IReadOnlySet<string> entityIds,
        List<string> errors)
    {
        if (!GranularPermissionActions.Field.Contains(grant.Action))
        {
            errors.Add($"Role '{roleId}' field grant action '{grant.Action}' is not supported; use read, write, or effect_write.");
        }

        ValidateEntityReference(roleId, grant, entityIds, allowWildcard: false, errors);
        var entity = bundle.Modules.SelectMany(module => module.Entities).FirstOrDefault(candidate =>
            candidate.Id.Equals(grant.Entity, StringComparison.OrdinalIgnoreCase) ||
            candidate.Name.Equals(grant.Entity, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(grant.Target))
        {
            errors.Add($"Role '{roleId}' field grant target is required.");
        }
        else if (entity is not null && !entity.Fields.Any(field =>
                     field.Id.Equals(grant.Target, StringComparison.OrdinalIgnoreCase) ||
                     field.Name.Equals(grant.Target, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Role '{roleId}' field grant references unknown field '{grant.Entity}.{grant.Target}'.");
        }
        else if (entity is not null &&
                 grant.Action.Equals("write", StringComparison.OrdinalIgnoreCase) &&
                 IsNamedActionLifecycle(entity.Fields.First(field =>
                         field.Id.Equals(grant.Target, StringComparison.OrdinalIgnoreCase) ||
                         field.Name.Equals(grant.Target, StringComparison.OrdinalIgnoreCase))
                     .Lifecycle?.Transitions))
        {
            errors.Add(
                $"Role '{roleId}' must not receive direct field write authority for named-action lifecycle field '{grant.Entity}.{grant.Target}'; use effect_write for an exact named action.");
        }
    }

    private static void ValidatePageGrant(
        ErpBundle bundle,
        string roleId,
        ErpGrant grant,
        IReadOnlySet<string> entityIds,
        List<string> errors)
    {
        if (!GranularPermissionActions.Page.Contains(grant.Action))
        {
            errors.Add($"Role '{roleId}' page grant action '{grant.Action}' is not supported; use view.");
        }

        if (!RuntimePagePermissionKinds.Supported.Contains(grant.Target))
        {
            errors.Add($"Role '{roleId}' page grant target '{grant.Target}' is not a supported generated page.");
            return;
        }

        if (string.IsNullOrWhiteSpace(grant.Entity))
        {
            errors.Add($"Role '{roleId}' page grant resource is required; use '*' for an unscoped generated page.");
            return;
        }

        var target = grant.Target.Trim().ToLowerInvariant();
        if (target is "entity_list" or "entity_create" or "entity_detail")
        {
            if (grant.Entity != "*" && !entityIds.Contains(grant.Entity))
            {
                errors.Add($"Role '{roleId}' page grant references unknown entity resource '{grant.Entity}'.");
            }
        }
        else if (target == "report_detail" &&
                 grant.Entity != "*" &&
                 !bundle.Reports.Any(report => report.Key.Equals(grant.Entity, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Role '{roleId}' page grant references unknown report resource '{grant.Entity}'.");
        }
        else if (target is "dashboard" or "reports" && grant.Entity != "*")
        {
            errors.Add($"Role '{roleId}' page grant '{target}' must use resource '*'.");
        }
    }

    private static void ValidateActionGrant(
        ErpBundle bundle,
        string roleId,
        ErpGrant grant,
        IReadOnlySet<string> entityIds,
        List<string> errors)
    {
        if (!GranularPermissionActions.Action.Contains(grant.Action))
        {
            errors.Add($"Role '{roleId}' named-action grant action '{grant.Action}' is not supported; use execute.");
        }

        ValidateEntityReference(roleId, grant, entityIds, allowWildcard: false, errors);
        if (string.IsNullOrWhiteSpace(grant.Target))
        {
            errors.Add($"Role '{roleId}' named-action grant target is required.");
            return;
        }

        var action = bundle.Actions.FirstOrDefault(candidate =>
            candidate.Key.Equals(grant.Target, StringComparison.OrdinalIgnoreCase));
        if (action is null)
        {
            errors.Add($"Role '{roleId}' named-action grant references unknown action '{grant.Target}'.");
        }
        else if (!action.Entity.Equals(grant.Entity, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Role '{roleId}' named-action grant '{grant.Target}' targets entity '{grant.Entity}' but the action belongs to '{action.Entity}'.");
        }
    }

    private static void ValidateRecordGrant(
        ErpBundle bundle,
        string roleId,
        ErpGrant grant,
        IReadOnlySet<string> entityIds,
        List<string> errors)
    {
        if (!RecordPermissionActions.Supported.Contains(grant.Action))
        {
            errors.Add(
                $"Role '{roleId}' record grant action '{grant.Action}' is not supported; use read, create, update, archive, restore, start_workflow, approve_task, or reject_task.");
        }

        ValidateEntityReference(roleId, grant, entityIds, allowWildcard: false, errors);
        if (!string.IsNullOrWhiteSpace(grant.Target))
        {
            errors.Add($"Role '{roleId}' record grant must not declare target '{grant.Target}'.");
        }

        if (grant.RecordScope is null)
        {
            errors.Add($"Role '{roleId}' record grant must declare a typed recordScope.");
            return;
        }

        var path = $"role[{roleId}].grant.recordScope";
        RequireIdentifier(grant.RecordScope.Field, $"{path}.field", errors);
        RequireIdentifier(grant.RecordScope.Source, $"{path}.source", errors);
        if (!string.IsNullOrWhiteSpace(grant.RecordScope.LinkedEntity))
        {
            RequireIdentifier(grant.RecordScope.LinkedEntity, $"{path}.linkedEntity", errors);
        }

        if (!string.IsNullOrWhiteSpace(grant.RecordScope.LinkedField))
        {
            RequireIdentifier(grant.RecordScope.LinkedField, $"{path}.linkedField", errors);
        }

        var source = NormalizeValue(grant.RecordScope.Source);
        if (!RecordPermissionScopeSources.Supported.Contains(source))
        {
            errors.Add($"Role '{roleId}' record scope source '{grant.RecordScope.Source}' is not supported; use actor_principal or linked_record.");
            return;
        }

        var entity = bundle.Modules
            .SelectMany(module => module.Entities)
            .FirstOrDefault(candidate => candidate.Id.Equals(grant.Entity, StringComparison.OrdinalIgnoreCase));
        if (entity is null)
        {
            return;
        }

        if (source == RecordPermissionScopeSources.ActorPrincipal)
        {
            ValidateActorPrincipalScope(roleId, entity, grant.RecordScope, errors);
            return;
        }

        ValidateLinkedRecordScope(bundle, roleId, entity, grant.RecordScope, errors);
    }

    private static void ValidateActorPrincipalScope(
        string roleId,
        ErpEntity entity,
        ErpRecordScope recordScope,
        List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(recordScope.LinkedEntity) ||
            !string.IsNullOrWhiteSpace(recordScope.LinkedField))
        {
            errors.Add(
                $"Role '{roleId}' actor_principal record scope must not declare linkedEntity or linkedField.");
        }

        if (string.Equals(recordScope.Field, "created_by", StringComparison.Ordinal))
        {
            return;
        }

        var field = entity.Fields.FirstOrDefault(candidate =>
            candidate.Id.Equals(recordScope.Field, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            errors.Add(
                $"Role '{roleId}' actor_principal record scope references unknown field '{entity.Id}.{recordScope.Field}'; use created_by or a declared integer field id.");
            return;
        }

        if (field.Computed is not null || !field.Type.Equals("integer", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Role '{roleId}' actor_principal record scope field '{entity.Id}.{field.Id}' must be a declared noncomputed integer field.");
        }
    }

    private static void ValidateLinkedRecordScope(
        ErpBundle bundle,
        string roleId,
        ErpEntity entity,
        ErpRecordScope recordScope,
        List<string> errors)
    {
        var governedField = entity.Fields.FirstOrDefault(candidate =>
            candidate.Id.Equals(recordScope.Field, StringComparison.OrdinalIgnoreCase));
        if (governedField is null)
        {
            errors.Add(
                $"Role '{roleId}' linked_record scope references unknown governed field '{entity.Id}.{recordScope.Field}'.");
            return;
        }

        if (governedField.Computed is not null ||
            !governedField.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(governedField.References))
        {
            errors.Add(
                $"Role '{roleId}' linked_record scope governed field '{entity.Id}.{governedField.Id}' must be a declared noncomputed relation.");
            return;
        }

        if (string.IsNullOrWhiteSpace(recordScope.LinkedEntity))
        {
            errors.Add($"Role '{roleId}' linked_record scope must declare linkedEntity.");
            return;
        }

        var linkedEntity = bundle.Modules
            .SelectMany(module => module.Entities)
            .FirstOrDefault(candidate => candidate.Id.Equals(recordScope.LinkedEntity, StringComparison.Ordinal));
        if (linkedEntity is null)
        {
            errors.Add(
                $"Role '{roleId}' linked_record scope linkedEntity '{recordScope.LinkedEntity}' must be an exact canonical entity id.");
            return;
        }

        if (string.IsNullOrWhiteSpace(recordScope.LinkedField))
        {
            if (!governedField.References.Equals(linkedEntity.Id, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Role '{roleId}' linked_record scope entity '{linkedEntity.Id}' must exactly match governed relation target '{governedField.References}' when linkedField is omitted.");
            }

            return;
        }

        var linkedField = linkedEntity.Fields.FirstOrDefault(candidate =>
            candidate.Id.Equals(recordScope.LinkedField, StringComparison.OrdinalIgnoreCase));
        if (linkedField is null)
        {
            errors.Add(
                $"Role '{roleId}' linked_record scope references unknown linked field '{linkedEntity.Id}.{recordScope.LinkedField}'.");
            return;
        }

        if (linkedField.Computed is not null ||
            !linkedField.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(linkedField.References))
        {
            errors.Add(
                $"Role '{roleId}' linked_record scope linked field '{linkedEntity.Id}.{linkedField.Id}' must be exactly one declared noncomputed relation.");
            return;
        }

        if (!linkedField.References.Equals(governedField.References, StringComparison.Ordinal))
        {
            errors.Add(
                $"Role '{roleId}' linked_record scope linked field '{linkedEntity.Id}.{linkedField.Id}' targets '{linkedField.References}' instead of governed relation target '{governedField.References}'.");
        }
    }

    private static void ValidateEntityReference(
        string roleId,
        ErpGrant grant,
        IReadOnlySet<string> entityIds,
        bool allowWildcard,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(grant.Entity))
        {
            errors.Add($"Role '{roleId}' grant entity is required.");
        }
        else if (grant.Entity == "*" && !allowWildcard)
        {
            errors.Add($"Role '{roleId}' {NormalizeScope(grant.Scope)} grant must target one concrete entity.");
        }
        else if (grant.Entity != "*" && !entityIds.Contains(grant.Entity))
        {
            errors.Add($"Role '{roleId}' grant references unknown entity '{grant.Entity}'.");
        }
    }

    private static string NormalizeScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope)
            ? PermissionGrantScopes.Entity
            : scope.Trim().ToLowerInvariant();

    private static string GrantIdentity(ErpGrant grant)
    {
        var recordScope = grant.RecordScope;
        if (!NormalizeScope(grant.Scope).Equals(PermissionGrantScopes.Record, StringComparison.OrdinalIgnoreCase) &&
            recordScope is null)
        {
            return string.Join(
                ':',
                NormalizeScope(grant.Scope),
                NormalizeValue(grant.Action),
                NormalizeValue(grant.Entity),
                NormalizeValue(grant.Target));
        }

        return string.Join(
            ':',
            NormalizeScope(grant.Scope),
            NormalizeValue(grant.Action),
            NormalizeValue(grant.Entity),
            NormalizeValue(grant.Target),
            NormalizeValue(recordScope?.Field),
            NormalizeValue(recordScope?.Source),
            NormalizeValue(recordScope?.LinkedEntity),
            NormalizeValue(recordScope?.LinkedField));
    }

    private static string NormalizeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
