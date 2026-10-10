using System.Text.Json;
using ErpGenerator.Runtime.Shared.Bundles;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleLifecycleValidator
{
    public static void ValidateFunctionalClosure(
        ErpBundle bundle,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        List<string> errors)
    {
        var roles = EffectiveRoles(bundle);
        foreach (var entity in bundle.Modules.SelectMany(module => module.Entities))
        {
            foreach (var field in entity.Fields.Where(field =>
                         IsNamedActionLifecycle(field.Lifecycle?.Transitions)))
            {
                ValidateNamedActionLifecycle(bundle, roles, fieldsByEntity, entity, field, errors);
            }
        }
    }

    private static void ValidateNamedActionLifecycle(
        ErpBundle bundle,
        IReadOnlyDictionary<string, ErpRole> roles,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        ErpEntity entity,
        ErpField field,
        List<string> errors)
    {
        var initial = field.Options.FirstOrDefault(option =>
            option.Equals(field.Lifecycle!.Initial?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (initial is null)
        {
            // The field-shape validator reports the invalid initial value. A
            // functional graph cannot be derived until that shape is valid.
            return;
        }

        var noninitial = field.Options
            .Where(option => !option.Equals(initial, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fieldRef = $"{entity.Id}.{field.Id}";
        if (noninitial.Count == 0)
        {
            errors.Add(
                $"Named-action lifecycle field '{fieldRef}' must declare at least one noninitial enum option.");
            return;
        }

        var transitionActions = FindTransitionActions(
            bundle.Actions,
            fieldsByEntity,
            entity.Id,
            field,
            noninitial);
        if (transitionActions.Count == 0)
        {
            errors.Add(
                $"Named-action lifecycle field '{fieldRef}' requires at least one typed action that assigns a declared noninitial enum option.");
            return;
        }

        if (transitionActions.Any(candidate =>
                ActionDeclaresExecutionRequirements(candidate.Action, candidate.SourceEntity, entity.Id, fieldsByEntity) &&
                roles.Values.Any(role => HasExecutableRoleClosure(
                    role,
                    candidate.Action,
                    candidate.SourceEntity,
                    entity.Id,
                    field))))
        {
            return;
        }

        errors.Add(
            $"Named-action lifecycle field '{fieldRef}' has transition action(s) " +
            $"'{string.Join("', '", transitionActions.Select(candidate => candidate.Action.Key).Distinct(StringComparer.OrdinalIgnoreCase))}' " +
            "but no executable role closure. The action must declare exact source read and target effect_update requirements, " +
            "and one role must hold source read plus exact named-action execute, target effect_update, and lifecycle-field effect_write grants.");
    }

    private static IReadOnlyList<TransitionAction> FindTransitionActions(
        IReadOnlyList<ErpAction> actions,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity,
        string lifecycleEntity,
        ErpField lifecycleField,
        IReadOnlySet<string> noninitialOptions)
    {
        var result = new List<TransitionAction>();
        foreach (var action in actions)
        {
            var sourceEntity = ResolveEntityId(action.Entity, fieldsByEntity);
            if (sourceEntity is null)
            {
                continue;
            }

            var analysis = ActionPermissionEffectAnalyzer.Analyze(
                sourceEntity,
                action.Steps.Select(step => new ActionPermissionEffectStep(
                        step.Id,
                        step.Type,
                        CanonicalEntity(step.Entity, fieldsByEntity),
                        step.ForEach?.Result,
                        step.RecordsFrom?.Result))
                    .ToArray());
            if (!analysis.IsValid)
            {
                continue;
            }

            foreach (var step in action.Steps)
            {
                var mutationTargets = analysis.Requirements
                    .Where(requirement =>
                        requirement.StepId.Equals(step.Id, StringComparison.OrdinalIgnoreCase) &&
                        requirement.Action.Equals("update", StringComparison.OrdinalIgnoreCase))
                    .Select(requirement => ResolveEntityId(requirement.Entity, fieldsByEntity))
                    .Where(target => target is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (mutationTargets.Length != 1 ||
                    !mutationTargets[0]!.Equals(lifecycleEntity, StringComparison.OrdinalIgnoreCase) ||
                    !StepAssignsNoninitialOption(step, lifecycleField, fieldsByEntity[lifecycleEntity], noninitialOptions))
                {
                    continue;
                }

                result.Add(new TransitionAction(action, sourceEntity));
                break;
            }
        }

        return result;
    }

    private static bool StepAssignsNoninitialOption(
        ErpActionStep step,
        ErpField lifecycleField,
        IReadOnlyDictionary<string, ErpField> targetFields,
        IReadOnlySet<string> noninitialOptions)
    {
        foreach (var (fieldKey, value) in step.Set)
        {
            if (!targetFields.TryGetValue(fieldKey, out var field) ||
                !field.Id.Equals(lifecycleField.Id, StringComparison.OrdinalIgnoreCase) ||
                !TryReadExactLiteral(value, out var literal))
            {
                continue;
            }

            return noninitialOptions.Contains(literal);
        }

        return false;
    }

    private static bool TryReadExactLiteral(ErpActionValue value, out string literal)
    {
        literal = string.Empty;
        if (value.Value is null ||
            value.ValueFrom is not null ||
            !string.IsNullOrWhiteSpace(value.Template) ||
            value.Aggregate is not null)
        {
            return false;
        }

        literal = value.Value switch
        {
            string text => text.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim() ?? string.Empty,
            _ => string.Empty
        };
        return literal.Length > 0;
    }

    private static bool ActionDeclaresExecutionRequirements(
        ErpAction action,
        string sourceEntity,
        string targetEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity) =>
        HasActionRequirement(action, "read", sourceEntity, fieldsByEntity) &&
        HasActionRequirement(
            action,
            ActionPermissionEffectAnalyzer.EffectUpdate,
            targetEntity,
            fieldsByEntity);

    private static bool HasActionRequirement(
        ErpAction action,
        string requiredAction,
        string requiredEntity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity) =>
        action.Requires.Permissions.Any(permission =>
            permission.Action.Equals(requiredAction, StringComparison.OrdinalIgnoreCase) &&
            NormalizeScope(permission.Scope).Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(permission.Target) &&
            permission.RecordScope is null &&
            string.Equals(
                CanonicalEntity(permission.Entity, fieldsByEntity),
                requiredEntity,
                StringComparison.OrdinalIgnoreCase));

    private static bool HasExecutableRoleClosure(
        ErpRole role,
        ErpAction action,
        string sourceEntity,
        string targetEntity,
        ErpField lifecycleField) =>
        HasSourceRead(role, sourceEntity) &&
        role.Grants.Any(grant =>
            NormalizeScope(grant.Scope).Equals(PermissionGrantScopes.Action, StringComparison.OrdinalIgnoreCase) &&
            grant.Action.Equals(GranularPermissionActions.Execute, StringComparison.OrdinalIgnoreCase) &&
            grant.Entity.Equals(sourceEntity, StringComparison.OrdinalIgnoreCase) &&
            grant.Target.Equals(action.Key, StringComparison.OrdinalIgnoreCase)) &&
        role.Grants.Any(grant =>
            NormalizeScope(grant.Scope).Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase) &&
            grant.Action.Equals(ActionPermissionEffectAnalyzer.EffectUpdate, StringComparison.OrdinalIgnoreCase) &&
            grant.Entity.Equals(targetEntity, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(grant.Target)) &&
        role.Grants.Any(grant =>
            NormalizeScope(grant.Scope).Equals(PermissionGrantScopes.Field, StringComparison.OrdinalIgnoreCase) &&
            grant.Action.Equals(GranularPermissionActions.EffectWrite, StringComparison.OrdinalIgnoreCase) &&
            grant.Entity.Equals(targetEntity, StringComparison.OrdinalIgnoreCase) &&
            (grant.Target.Equals(lifecycleField.Id, StringComparison.OrdinalIgnoreCase) ||
             grant.Target.Equals(lifecycleField.Name, StringComparison.OrdinalIgnoreCase)));

    private static bool HasSourceRead(ErpRole role, string sourceEntity) =>
        role.Grants.Any(grant =>
            (NormalizeScope(grant.Scope) is PermissionGrantScopes.Entity or PermissionGrantScopes.Record) &&
            (grant.Action is "*" || grant.Action.Equals("read", StringComparison.OrdinalIgnoreCase)) &&
            (grant.Entity is "*" || grant.Entity.Equals(sourceEntity, StringComparison.OrdinalIgnoreCase)));

    private static string? CanonicalEntity(
        string? entity,
        IReadOnlyDictionary<string, Dictionary<string, ErpField>> fieldsByEntity) =>
        string.IsNullOrWhiteSpace(entity)
            ? null
            : ResolveEntityId(entity, fieldsByEntity) ?? entity.Trim();

    private static string NormalizeScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope)
            ? PermissionGrantScopes.Entity
            : scope.Trim().ToLowerInvariant();

    private sealed record TransitionAction(ErpAction Action, string SourceEntity);
}
