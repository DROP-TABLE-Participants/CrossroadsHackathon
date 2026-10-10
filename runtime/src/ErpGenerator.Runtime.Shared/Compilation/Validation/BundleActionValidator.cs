using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleActionValueValidator;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleActionValidator
{
    public static void ValidateActions(
        ErpBundle bundle,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var actionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < bundle.Actions.Count; index++)
        {
            var action = bundle.Actions[index];
            var path = $"actions[{index}]";
            RequireIdentifier(action.Key, $"{path}.key", errors);
            RequireText(action.Label, $"{path}.label", errors);
            RejectSqlLikeContent(action.Key, $"{path}.key", errors);
            RejectSqlLikeContent(action.Label, $"{path}.label", errors);
            RejectSqlLikeContent(action.Entity, $"{path}.entity", errors);
            RejectSqlLikeContent(action.Type, $"{path}.type", errors);
            RejectSqlLikeContent(action.Description, $"{path}.description", errors);

            if (!actionKeys.Add(action.Key))
            {
                errors.Add($"Duplicate action key '{action.Key}'.");
            }

            var type = string.IsNullOrWhiteSpace(action.Type) ? "record_action" : action.Type.Trim().ToLowerInvariant();
            if (!ActionTypes.Contains(type))
            {
                errors.Add($"Action '{action.Key}' uses unsupported type '{action.Type}'.");
            }

            var sourceEntity = ResolveEntityId(action.Entity, fieldsByEntity);
            if (sourceEntity is null)
            {
                errors.Add($"Action '{action.Key}' references unknown entity '{action.Entity}'.");
                continue;
            }

            for (var visibleIndex = 0; visibleIndex < action.VisibleWhen.Count; visibleIndex++)
            {
                ValidateActionCondition(
                    action.Key,
                    action.VisibleWhen[visibleIndex],
                    $"{path}.visible_when[{visibleIndex}]",
                    sourceEntity,
                    fieldsByEntity,
                    new Dictionary<string, ActionStepContext>(StringComparer.OrdinalIgnoreCase),
                    itemEntity: null,
                    errors);
            }

            if (action.Requires.Permissions.Count == 0)
            {
                errors.Add($"Action '{action.Key}' must declare at least one permission requirement.");
            }

            for (var permissionIndex = 0; permissionIndex < action.Requires.Permissions.Count; permissionIndex++)
            {
                var permission = action.Requires.Permissions[permissionIndex];
                var permissionPath = $"{path}.requires.permissions[{permissionIndex}]";
                RejectSqlLikeContent(permission.Entity, $"{permissionPath}.entity", errors);
                var permissionScope = string.IsNullOrWhiteSpace(permission.Scope)
                    ? PermissionGrantScopes.Entity
                    : permission.Scope.Trim().ToLowerInvariant();
                if (!permissionScope.Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"Action '{action.Key}' permission requirements must use scope 'entity'; scope '{permission.Scope}' is not preserved in executable action metadata.");
                }

                if (permissionScope.Equals(PermissionGrantScopes.Record, StringComparison.OrdinalIgnoreCase) ||
                    permission.RecordScope is not null)
                {
                    errors.Add(
                        $"Action '{action.Key}' permission requirements must not declare record-scoped permissions; record scope is enforced from the actor's role grants.");
                }

                if (!string.IsNullOrWhiteSpace(permission.Target))
                {
                    errors.Add(
                        $"Action '{action.Key}' permission requirements must not declare target '{permission.Target}'; requirements contain only exact entity actions.");
                }

                if (permission.Action == "*")
                {
                    errors.Add($"Action '{action.Key}' permission requirements must declare a concrete action.");
                }
                else if (!PermissionActions.Contains(permission.Action))
                {
                    errors.Add($"Action '{action.Key}' permission action '{permission.Action}' is not supported.");
                }

                if (string.IsNullOrWhiteSpace(permission.Entity) || ResolveEntityId(permission.Entity, fieldsByEntity) is null)
                {
                    errors.Add($"Action '{action.Key}' permission references unknown entity '{permission.Entity}'.");
                }
            }

            if (action.Steps.Count == 0)
            {
                errors.Add($"Action '{action.Key}' must declare at least one step.");
            }

            var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var priorSteps = new Dictionary<string, ActionStepContext>(StringComparer.OrdinalIgnoreCase);
            for (var stepIndex = 0; stepIndex < action.Steps.Count; stepIndex++)
            {
                ValidateActionStep(action, action.Steps[stepIndex], stepIndex, sourceEntity, fieldsByEntity, stepIds, priorSteps, errors);
            }

            ValidatePermissionEffects(action, sourceEntity, fieldsByEntity, errors);
        }
    }

    private static void ValidatePermissionEffects(
        ErpAction action,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var steps = action.Steps
            .Select(step => new ActionPermissionEffectStep(
                step.Id,
                step.Type,
                CanonicalEntity(step.Entity, fieldsByEntity),
                step.ForEach?.Result,
                step.RecordsFrom?.Result))
            .ToArray();
        var analysis = ActionPermissionEffectAnalyzer.Analyze(sourceEntity, steps);
        foreach (var error in analysis.Errors)
        {
            errors.Add($"Action '{action.Key}' has invalid permission effects. {error}");
        }

        var declared = action.Requires.Permissions
            .Select(permission => new
            {
                Action = permission.Action.Trim(),
                Entity = CanonicalEntity(permission.Entity, fieldsByEntity)
            })
            .Where(permission => permission.Entity is not null)
            .ToArray();

        foreach (var requirement in analysis.Requirements)
        {
            var matching = declared.Where(permission =>
                    ActionPermissionEffectAnalyzer.IsExactDeclaredActionForRequirement(
                        permission.Action,
                        requirement.Action) &&
                    ActionPermissionEffectAnalyzer.EntityMatches(permission.Entity!, requirement.Entity))
                .ToArray();
            if (matching.Length == 1)
            {
                continue;
            }

            var effectAction = ActionPermissionEffectAnalyzer.EffectActionFor(requirement.Action);
            errors.Add(matching.Length == 0
                ? $"Action '{action.Key}' step '{requirement.StepId}' requires exactly one permission '{requirement.Action}'" +
                  (effectAction is null ? string.Empty : $" or '{effectAction}'") +
                  $" on entity '{requirement.Entity}', but actions[{action.Key}].requires.permissions does not declare it."
                : $"Action '{action.Key}' step '{requirement.StepId}' ambiguously declares both or duplicate direct/effect permissions for '{requirement.Action}' on entity '{requirement.Entity}'.");
        }

        foreach (var permission in declared.Where(permission =>
                     ActionPermissionEffectAnalyzer.IsMutationAction(permission.Action)))
        {
            var directAction = ActionPermissionEffectAnalyzer.DirectActionFor(permission.Action)!;
            if (!analysis.Requirements.Any(requirement =>
                    requirement.Action.Equals(directAction, StringComparison.OrdinalIgnoreCase) &&
                    ActionPermissionEffectAnalyzer.EntityMatches(permission.Entity!, requirement.Entity)))
            {
                errors.Add(
                    $"Action '{action.Key}' declares mutation permission '{permission.Action}' on entity '{permission.Entity}' without one exact matching mutation effect.");
            }
        }
    }

    private static string? CanonicalEntity(
        string? entity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity) =>
        string.IsNullOrWhiteSpace(entity) ? null : ResolveEntityId(entity, fieldsByEntity) ?? entity.Trim();

    static void ValidateActionStep(
        ErpAction action,
        ErpActionStep step,
        int stepIndex,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        HashSet<string> stepIds,
        Dictionary<string, ActionStepContext> priorSteps,
        List<string> errors)
    {
        var path = $"action[{action.Key}].steps[{stepIndex}]";
        RequireIdentifier(step.Id, $"{path}.id", errors);
        RejectSqlLikeContent(step.Id, $"{path}.id", errors);
        RejectSqlLikeContent(step.Type, $"{path}.type", errors);
        RejectSqlLikeContent(step.Entity, $"{path}.entity", errors);
        RejectSqlLikeContent(step.Error, $"{path}.error", errors);
        RejectSqlLikeContent(step.Event, $"{path}.event", errors);
        RejectSqlLikeContent(step.Message, $"{path}.message", errors);

        if (!stepIds.Add(step.Id))
        {
            errors.Add($"Action '{action.Key}' contains duplicate step id '{step.Id}'.");
        }

        var type = step.Type.Trim().ToLowerInvariant();
        if (!ActionStepTypes.Contains(type))
        {
            errors.Add($"Action '{action.Key}' step '{step.Id}' uses unsupported type '{step.Type}'.");
        }

        switch (type)
        {
            case "query_records":
                {
                    var targetEntity = ResolveEntityId(step.Entity, fieldsByEntity);
                    if (targetEntity is null)
                    {
                        errors.Add($"Action '{action.Key}' query_records step '{step.Id}' references unknown entity '{step.Entity}'.");
                        break;
                    }

                    ValidateActionFilters(action.Key, step, targetEntity, sourceEntity, fieldsByEntity, priorSteps, errors, path);
                    priorSteps[step.Id] = new ActionStepContext(type, targetEntity);
                    break;
                }
            case "guard":
                if (step.Condition is null)
                {
                    errors.Add($"Action '{action.Key}' guard step '{step.Id}' must declare condition.");
                }
                else
                {
                    ValidateActionCondition(action.Key, step.Condition, $"{path}.condition", sourceEntity, fieldsByEntity, priorSteps, itemEntity: null, errors);
                }

                if (string.IsNullOrWhiteSpace(step.Error))
                {
                    errors.Add($"Action '{action.Key}' guard step '{step.Id}' must declare error.");
                }

                priorSteps[step.Id] = new ActionStepContext(type, sourceEntity);
                break;
            case "create_record":
                {
                    var targetEntity = ResolveEntityId(step.Entity, fieldsByEntity);
                    if (targetEntity is null)
                    {
                        errors.Add($"Action '{action.Key}' create_record step '{step.Id}' references unknown entity '{step.Entity}'.");
                        break;
                    }

                    ValidateActionValuesForEntity(action.Key, step.Id, step.Values, $"{path}.values", targetEntity, sourceEntity, fieldsByEntity, priorSteps, itemEntity: null, errors, requireCreateFields: true);
                    priorSteps[step.Id] = new ActionStepContext(type, targetEntity);
                    break;
                }
            case "create_records_for_each":
                {
                    var targetEntity = ResolveEntityId(step.Entity, fieldsByEntity);
                    if (targetEntity is null)
                    {
                        errors.Add($"Action '{action.Key}' create_records_for_each step '{step.Id}' references unknown entity '{step.Entity}'.");
                        break;
                    }

                    var itemEntity = ValidateForEach(action.Key, step, path, priorSteps, errors);
                    ValidateActionValuesForEntity(action.Key, step.Id, step.Values, $"{path}.values", targetEntity, sourceEntity, fieldsByEntity, priorSteps, itemEntity, errors, requireCreateFields: true);
                    priorSteps[step.Id] = new ActionStepContext(type, targetEntity);
                    break;
                }
            case "update_records":
                {
                    var itemEntity = ValidateRecordsFrom(action.Key, step, path, priorSteps, errors);
                    if (itemEntity is not null)
                    {
                        ValidateActionValuesForEntity(action.Key, step.Id, step.Set, $"{path}.set", itemEntity, sourceEntity, fieldsByEntity, priorSteps, itemEntity: null, errors, requireCreateFields: false);
                        ValidateNamedActionLifecycleUpdate(action, step, itemEntity, fieldsByEntity, errors);
                    }

                    priorSteps[step.Id] = new ActionStepContext(type, itemEntity ?? sourceEntity);
                    break;
                }
            case "update_current_record":
                {
                    if (step.Set.Count == 0)
                    {
                        errors.Add($"Action '{action.Key}' update_current_record step '{step.Id}' must declare set values.");
                    }

                    ValidateActionValuesForEntity(action.Key, step.Id, step.Set, $"{path}.set", sourceEntity, sourceEntity, fieldsByEntity, priorSteps, itemEntity: null, errors, requireCreateFields: false);
                    ValidateNamedActionLifecycleUpdate(action, step, sourceEntity, fieldsByEntity, errors);
                    break;
                }
            case "audit_event":
                if (string.IsNullOrWhiteSpace(step.Event))
                {
                    errors.Add($"Action '{action.Key}' audit_event step '{step.Id}' must declare event.");
                }
                else if (!EventNamePattern.IsMatch(step.Event))
                {
                    errors.Add($"Action '{action.Key}' audit_event step '{step.Id}' event must be dotted lower_snake_case.");
                }

                ValidateActionValueDictionary(action.Key, step.Id, step.Metadata, $"{path}.metadata", sourceEntity, fieldsByEntity, priorSteps, itemEntity: null, errors);
                priorSteps[step.Id] = new ActionStepContext(type, sourceEntity);
                break;
            case "return_result":
                if (step.Return is null)
                {
                    errors.Add($"Action '{action.Key}' return_result step '{step.Id}' must declare return.");
                }
                else
                {
                    RejectSqlLikeContent(step.Return.Step, $"{path}.return.step", errors);
                    RejectSqlLikeContent(step.Return.Entity, $"{path}.return.entity", errors);
                    RejectSqlLikeContent(step.Return.Message, $"{path}.return.message", errors);
                    if (!string.IsNullOrWhiteSpace(step.Return.Step) && !priorSteps.ContainsKey(step.Return.Step))
                    {
                        errors.Add($"Action '{action.Key}' return_result step '{step.Id}' references unknown prior step '{step.Return.Step}'.");
                    }

                    if (!string.IsNullOrWhiteSpace(step.Return.Entity) && ResolveEntityId(step.Return.Entity, fieldsByEntity) is null)
                    {
                        errors.Add($"Action '{action.Key}' return_result step '{step.Id}' references unknown entity '{step.Return.Entity}'.");
                    }
                }

                priorSteps[step.Id] = new ActionStepContext(type, sourceEntity);
                break;
        }
    }

    private static void ValidateNamedActionLifecycleUpdate(
        ErpAction action,
        ErpActionStep step,
        string targetEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var targetFields = fieldsByEntity[targetEntity];
        foreach (var fieldName in step.Set.Keys)
        {
            if (!targetFields.TryGetValue(fieldName, out var field) ||
                !IsNamedActionLifecycle(field.Lifecycle?.Transitions))
            {
                continue;
            }

            var hasEffectUpdate = action.Requires.Permissions.Any(permission =>
                permission.Action.Equals(ActionPermissionEffectAnalyzer.EffectUpdate, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    CanonicalEntity(permission.Entity, fieldsByEntity),
                    targetEntity,
                    StringComparison.OrdinalIgnoreCase));
            if (!hasEffectUpdate)
            {
                errors.Add(
                    $"Action '{action.Key}' step '{step.Id}' can transition named-action lifecycle field '{targetEntity}.{field.Id}' only with exact permission 'effect_update' on entity '{targetEntity}'.");
            }
        }
    }

    static void ValidateActionFilters(
        string actionKey,
        ErpActionStep step,
        string targetEntity,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        List<string> errors,
        string path)
    {
        if (step.Where.Count == 0)
        {
            return;
        }

        var targetFields = fieldsByEntity[targetEntity];
        for (var index = 0; index < step.Where.Count; index++)
        {
            var filter = step.Where[index];
            var filterPath = $"{path}.where[{index}]";
            RequireIdentifier(filter.Field, $"{filterPath}.field", errors);
            RejectSqlLikeContent(filter.Field, $"{filterPath}.field", errors);
            RejectSqlLikeContent(filter.Op, $"{filterPath}.op", errors);
            RejectSqlLikeValue(filter.Value, $"{filterPath}.value", errors);
            RejectSqlLikeValue(filter.Values, $"{filterPath}.values", errors);

            if (filter.Field.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                ValidateActionSystemIdFilter(
                    actionKey,
                    step.Id,
                    filter,
                    filterPath,
                    targetEntity,
                    sourceEntity,
                    fieldsByEntity,
                    priorSteps,
                    errors);
                continue;
            }

            if (!targetFields.TryGetValue(filter.Field, out var field))
            {
                errors.Add($"Action '{actionKey}' step '{step.Id}' filter references unknown field '{filter.Field}' on entity '{targetEntity}'.");
                continue;
            }

            if (field.Computed is not null)
            {
                errors.Add($"Action '{actionKey}' step '{step.Id}' filter cannot target computed field '{targetEntity}.{field.Id}' because query_records filters require a materialized database field.");
                continue;
            }

            var op = filter.Op.Trim().ToLowerInvariant();
            if (!ReportFilterOps.Contains(op))
            {
                errors.Add($"Action '{actionKey}' step '{step.Id}' filter on '{filter.Field}' uses unsupported op '{filter.Op}'.");
                continue;
            }

            if (op is "exists" or "not_exists")
            {
                continue;
            }

            var hasValueFrom = filter.ValueFrom is not null && HasAnyValueFrom(filter.ValueFrom);
            var values = filter.Values.Count > 0 ? filter.Values : TryGetArrayValue(filter.Value, out var arrayValues) ? arrayValues : [];
            if (op is "in" or "not_in")
            {
                if (values.Count == 0 && !hasValueFrom)
                {
                    errors.Add($"Action '{actionKey}' step '{step.Id}' filter op '{op}' requires values or value_from.");
                    continue;
                }

                foreach (var value in values)
                {
                    if (!IsValueCompatible(field, value))
                    {
                        errors.Add($"Action '{actionKey}' step '{step.Id}' filter op '{op}' contains a value incompatible with field '{field.Name}'.");
                        break;
                    }
                }
            }
            else if (filter.Value is null && !hasValueFrom)
            {
                errors.Add($"Action '{actionKey}' step '{step.Id}' filter op '{op}' requires value or value_from.");
            }
            else if (filter.Value is not null && !IsValueCompatible(field, filter.Value))
            {
                errors.Add($"Action '{actionKey}' step '{step.Id}' filter compares field '{field.Name}' to an incompatible value.");
            }

            if (filter.ValueFrom is not null)
            {
                ValidateActionValueFrom(
                    actionKey,
                    step.Id,
                    filter.ValueFrom,
                    $"{filterPath}.value_from",
                    sourceEntity,
                    fieldsByEntity,
                    priorSteps,
                    itemEntity: null,
                    targetField: field,
                    errors,
                    comparisonOp: op);
            }
        }
    }

    static void ValidateActionSystemIdFilter(
        string actionKey,
        string stepId,
        ErpActionFilter filter,
        string filterPath,
        string targetEntity,
        string sourceEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        List<string> errors)
    {
        if (!filter.Op.Trim().Equals("eq", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Action '{actionKey}' step '{stepId}' system id filter must use exact operator 'eq'.");
        }

        if (filter.Values.Count > 0 || TryGetArrayValue(filter.Value, out _))
        {
            errors.Add(
                $"Action '{actionKey}' step '{stepId}' system id filter must compare one exact identity, not a value list.");
        }

        var hasValueFrom = filter.ValueFrom is not null && HasAnyValueFrom(filter.ValueFrom);
        if (filter.Value is not null || !hasValueFrom)
        {
            errors.Add(
                $"Action '{actionKey}' step '{stepId}' system id filter must use one relation-bound value_from.record identity; literal ids are forbidden.");
            return;
        }

        var valueFrom = filter.ValueFrom!;
        if (string.IsNullOrWhiteSpace(valueFrom.Record) ||
            !string.IsNullOrWhiteSpace(valueFrom.Item) ||
            !string.IsNullOrWhiteSpace(valueFrom.Step) ||
            !string.IsNullOrWhiteSpace(valueFrom.Now) ||
            !string.IsNullOrWhiteSpace(valueFrom.Field))
        {
            errors.Add(
                $"Action '{actionKey}' step '{stepId}' system id filter value_from must use one record field.");
            return;
        }

        // A system id represents the identity of the queried entity. Model it as
        // a typed relation target while validating value_from so record.id is
        // accepted only for the same entity and relation fields must point at the
        // exact queried entity.
        var identityTarget = new ErpField
        {
            Id = "id",
            Name = "id",
            DisplayName = "ID",
            Type = "relation",
            References = targetEntity
        };
        ValidateActionValueFrom(
            actionKey,
            stepId,
            valueFrom,
            $"{filterPath}.value_from",
            sourceEntity,
            fieldsByEntity,
            priorSteps,
            itemEntity: null,
            targetField: identityTarget,
            errors: errors);
    }

    static string? ValidateForEach(
        string actionKey,
        ErpActionStep step,
        string path,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        List<string> errors)
    {
        if (step.ForEach is null || string.IsNullOrWhiteSpace(step.ForEach.Result))
        {
            errors.Add($"Action '{actionKey}' create_records_for_each step '{step.Id}' must declare foreach.result.");
            return null;
        }

        RejectSqlLikeContent(step.ForEach.Result, $"{path}.foreach.result", errors);
        if (!priorSteps.TryGetValue(step.ForEach.Result, out var result) || !result.Type.Equals("query_records", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Action '{actionKey}' create_records_for_each step '{step.Id}' references unknown query result '{step.ForEach.Result}'.");
            return null;
        }

        return result.Entity;
    }

    static string? ValidateRecordsFrom(
        string actionKey,
        ErpActionStep step,
        string path,
        IReadOnlyDictionary<string, ActionStepContext> priorSteps,
        List<string> errors)
    {
        if (step.RecordsFrom is null || string.IsNullOrWhiteSpace(step.RecordsFrom.Result))
        {
            errors.Add($"Action '{actionKey}' update_records step '{step.Id}' must declare records_from.result.");
            return null;
        }

        RejectSqlLikeContent(step.RecordsFrom.Result, $"{path}.records_from.result", errors);
        if (!priorSteps.TryGetValue(step.RecordsFrom.Result, out var result) || !result.Type.Equals("query_records", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Action '{actionKey}' update_records step '{step.Id}' references unknown query result '{step.RecordsFrom.Result}'.");
            return null;
        }

        return result.Entity;
    }
}
