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
    private async Task<RuntimeActionAuthorizationDecision> EvaluateActionAuthorizationAsync(
        NpgsqlConnection connection,
        RuntimePermissionSnapshot permissionSnapshot,
        ActionDefinition action,
        RuntimeEntityDefinition sourceEntity,
        CancellationToken cancellationToken)
    {
        if (!IsStructurallyValid(action))
        {
            return RuntimeActionAuthorizationDecision.InvalidDefinition(
                "the persisted metadata is structurally invalid");
        }

        if (!EntityReferenceMatches(action.Entity, sourceEntity))
        {
            return RuntimeActionAuthorizationDecision.InvalidDefinition(
                "the persisted source entity does not match the action-definition row");
        }

        var effectSteps = new List<ActionPermissionEffectStep>(action.Steps.Count);
        foreach (var step in action.Steps)
        {
            if (step is null)
            {
                return RuntimeActionAuthorizationDecision.InvalidDefinition(
                    "the persisted action contains a null step");
            }

            effectSteps.Add(new ActionPermissionEffectStep(
                step.Id,
                step.Type,
                step.Entity,
                step.ForEach?.Result,
                step.RecordsFrom?.Result));
        }

        // The entity loaded from the action-definition row is the trust anchor.
        // Serialized metadata is validated against it above, but never selects the
        // permission target for update_current_record.
        var effects = ActionPermissionEffectAnalyzer.Analyze(sourceEntity.Id, effectSteps);
        if (!effects.IsValid)
        {
            return RuntimeActionAuthorizationDecision.InvalidDefinition(
                string.Join(" ", effects.Errors));
        }

        var entityCache = new Dictionary<string, RuntimeEntityDefinition?>(StringComparer.OrdinalIgnoreCase);
        entityCache[sourceEntity.Id] = sourceEntity;

        var declared = new List<(string Action, RuntimeEntityDefinition Entity)>(action.Permissions.Count);
        foreach (var permission in action.Permissions)
        {
            // Entity wildcards are rejected by the bundle compiler. Persisted
            // metadata must obey the same rule rather than becoming more
            // permissive after deployment.
            if (string.IsNullOrWhiteSpace(permission.Action) ||
                string.IsNullOrWhiteSpace(permission.Entity) ||
                permission.Action.Trim().Equals("*", StringComparison.Ordinal) ||
                permission.Entity.Trim().Equals("*", StringComparison.Ordinal))
            {
                return RuntimeActionAuthorizationDecision.InvalidDefinition(
                    "permission requirements must use exact actions and entities");
            }

            var entity = await GetEntityForPermissionAsync(
                connection,
                permission.Entity,
                entityCache,
                cancellationToken);
            if (entity is null)
            {
                return RuntimeActionAuthorizationDecision.InvalidDefinition(
                    $"permission requirement references unknown entity '{permission.Entity}'");
            }

            declared.Add((permission.Action.Trim(), entity));
        }

        var derived = new List<(string Action, RuntimeEntityDefinition Entity)>(effects.Requirements.Count);
        foreach (var effect in effects.Requirements)
        {
            var effectEntity = await GetEntityForPermissionAsync(
                connection,
                effect.Entity,
                entityCache,
                cancellationToken);
            var matching = effectEntity is null
                ? []
                : declared.Where(permission =>
                        ActionPermissionEffectAnalyzer.IsExactDeclaredActionForRequirement(
                            permission.Action,
                            effect.Action) &&
                        permission.Entity.Id.Equals(effectEntity.Id, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            if (effectEntity is null || matching.Length != 1)
            {
                return RuntimeActionAuthorizationDecision.InvalidDefinition(
                    effectEntity is null
                        ? $"action effect step '{effect.StepId}' references unknown entity '{effect.Entity}'"
                        : matching.Length == 0
                            ? $"action effect step '{effect.StepId}' is missing one exact direct/effect '{effect.Action}' permission requirement for entity '{effectEntity.Id}'"
                            : $"action effect step '{effect.StepId}' ambiguously declares direct/effect '{effect.Action}' permissions for entity '{effectEntity.Id}'");
            }


            var effectStepMatches = action.Steps
                .Where(step => step.Id.Equals(effect.StepId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (effectStepMatches.Length != 1)
            {
                return RuntimeActionAuthorizationDecision.InvalidDefinition(
                    $"action mutation effect references non-unique step '{effect.StepId}'");
            }

            var lifecycleError = ValidatePersistedLifecycleMutation(
                effectStepMatches[0],
                effectEntity,
                matching[0].Action,
                effect.Action);
            if (lifecycleError is not null)
            {
                return RuntimeActionAuthorizationDecision.InvalidDefinition(lifecycleError);
            }

            derived.Add((matching[0].Action, effectEntity));
        }

        foreach (var permission in declared.Where(permission =>
                     ActionPermissionEffectAnalyzer.IsMutationAction(permission.Action)))
        {
            var directAction = ActionPermissionEffectAnalyzer.DirectActionFor(permission.Action)!;
            if (!effects.Requirements.Any(effect =>
                    effect.Action.Equals(directAction, StringComparison.OrdinalIgnoreCase) &&
                    effect.Entity.Equals(permission.Entity.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return RuntimeActionAuthorizationDecision.InvalidDefinition(
                    $"mutation permission '{permission.Action}' on entity '{permission.Entity.Id}' has no exact matching mutation effect");
            }
        }

        if (!permissions.IsNamedActionAllowed(permissionSnapshot, sourceEntity, action.Key))
        {
            return RuntimeActionAuthorizationDecision.PermissionDenied(
                "named_action.execute",
                action.Key);
        }

        if (!permissions.IsAllowed(permissionSnapshot, "read", sourceEntity))
        {
            return RuntimeActionAuthorizationDecision.PermissionDenied(
                "read",
                sourceEntity.Id);
        }

        var checkedRequirements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var authorizedRequirements = new List<RuntimeActionAuthorizationRequirement>();
        foreach (var requirement in declared.Concat(derived))
        {
            var key = $"{requirement.Action}:{requirement.Entity.Id}";
            if (!checkedRequirements.Add(key))
            {
                continue;
            }

            if (!permissions.IsAllowed(permissionSnapshot, requirement.Action, requirement.Entity))
            {
                return RuntimeActionAuthorizationDecision.PermissionDenied(
                    requirement.Action.Trim().ToLowerInvariant(),
                    requirement.Entity.Id);
            }

            authorizedRequirements.Add(new RuntimeActionAuthorizationRequirement(
                requirement.Action.Trim().ToLowerInvariant(),
                requirement.Entity));
        }

        return RuntimeActionAuthorizationDecision.Allow(authorizedRequirements);
    }

    private static string? ValidatePersistedLifecycleMutation(
        ActionStepDefinition step,
        RuntimeEntityDefinition entity,
        string declaredMutationAction,
        string directMutationAction)
    {
        var values = directMutationAction.Equals("create", StringComparison.OrdinalIgnoreCase)
            ? step.Values
            : step.Set;
        foreach (var fieldKey in values.Keys)
        {
            var field = ResolveField(entity, fieldKey);
            if (field?.Lifecycle is null)
            {
                continue;
            }

            if (directMutationAction.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                return $"action step '{step.Id}' supplies server-initialized lifecycle field '{entity.Id}.{field.Name}' during create";
            }

            if (field.Lifecycle.RequiresNamedAction &&
                !declaredMutationAction.Equals(
                    ActionPermissionEffectAnalyzer.EffectUpdate,
                    StringComparison.OrdinalIgnoreCase))
            {
                return $"action step '{step.Id}' transitions named-action lifecycle field '{entity.Id}.{field.Name}' without effect_update";
            }
        }

        return null;
    }

    private static bool IsStructurallyValid(ActionDefinition action)
    {
        return !string.IsNullOrWhiteSpace(action.Key) &&
               !string.IsNullOrWhiteSpace(action.Label) &&
               !string.IsNullOrWhiteSpace(action.Entity) &&
               action.Type?.Trim().Equals("record_action", StringComparison.OrdinalIgnoreCase) == true &&
               action.VisibleWhen is not null &&
               action.Permissions is { Count: > 0 } &&
               action.Steps is { Count: > 0 } &&
               action.VisibleWhen.All(IsStructurallyValid) &&
               action.Permissions.All(permission =>
                   permission is not null &&
                   !string.IsNullOrWhiteSpace(permission.Action) &&
                   !string.IsNullOrWhiteSpace(permission.Entity)) &&
               action.Steps.All(IsStructurallyValid);
    }

    private static bool IsStructurallyValid(ActionStepDefinition? step)
    {
        if (step is null ||
            string.IsNullOrWhiteSpace(step.Id) ||
            string.IsNullOrWhiteSpace(step.Type) ||
            step.Where is null ||
            step.Values is null ||
            step.Set is null ||
            step.Metadata is null ||
            step.Where.Any(filter => filter is null || filter.Values is null) ||
            step.Values.Any(value => value.Value is null) ||
            step.Set.Any(value => value.Value is null) ||
            step.Metadata.Any(value => value.Value is null))
        {
            return false;
        }

        return step.Type.Trim().ToLowerInvariant() switch
        {
            "query_records" => !string.IsNullOrWhiteSpace(step.Entity),
            "guard" => step.Condition is not null &&
                       IsStructurallyValid(step.Condition) &&
                       !string.IsNullOrWhiteSpace(step.Error),
            "create_record" => !string.IsNullOrWhiteSpace(step.Entity),
            "create_records_for_each" => !string.IsNullOrWhiteSpace(step.Entity) &&
                                         !string.IsNullOrWhiteSpace(step.ForEach?.Result),
            "update_records" => !string.IsNullOrWhiteSpace(step.RecordsFrom?.Result),
            "update_current_record" => step.Set.Count > 0,
            "audit_event" => !string.IsNullOrWhiteSpace(step.Event),
            "return_result" => step.Return is not null,
            _ => false
        };
    }

    private static bool IsStructurallyValid(ActionConditionDefinition? condition) =>
        condition is not null &&
        !string.IsNullOrWhiteSpace(condition.Op) &&
        condition.Conditions is not null &&
        condition.Conditions.All(IsStructurallyValid);

    private async Task<RuntimeEntityDefinition?> GetEntityForPermissionAsync(
        NpgsqlConnection connection,
        string entityKey,
        Dictionary<string, RuntimeEntityDefinition?> entityCache,
        CancellationToken cancellationToken)
    {
        var cacheKey = entityKey.Trim();
        if (entityCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var entity = await LoadEntityByIdAsync(connection, cacheKey, cancellationToken);
        entityCache[cacheKey] = entity;
        return entity;
    }

    private static bool EntityReferenceMatches(string entityReference, RuntimeEntityDefinition entity) =>
        entityReference.Trim().Equals(entity.Id, StringComparison.Ordinal);

    private static string ResolveMutationPermissionAction(
        IReadOnlyList<RuntimeActionAuthorizationRequirement> requirements,
        RuntimeEntityDefinition entity,
        string directAction)
    {
        var effectAction = ActionPermissionEffectAnalyzer.EffectActionFor(directAction)
                           ?? throw new RuntimeActionValidationException(
                               $"Unsupported action mutation permission '{directAction}'.");
        var matching = requirements.Where(requirement =>
                requirement.Entity.Id.Equals(entity.Id, StringComparison.OrdinalIgnoreCase) &&
                (requirement.Action.Equals(directAction, StringComparison.OrdinalIgnoreCase) ||
                 requirement.Action.Equals(effectAction, StringComparison.OrdinalIgnoreCase)))
            .Select(requirement => requirement.Action.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matching.Length == 1
            ? matching[0]
            : throw new RuntimeActionValidationException(
                $"Action mutation on '{entity.Id}' does not resolve to one exact direct/effect '{directAction}' permission.");
    }

    private sealed record RuntimeActionAuthorizationDecision(
        bool Allowed,
        bool DefinitionValid,
        string PermissionAction,
        string Target,
        string Reason,
        IReadOnlyList<RuntimeActionAuthorizationRequirement> Requirements)
    {
        public static RuntimeActionAuthorizationDecision Allow(
            IReadOnlyList<RuntimeActionAuthorizationRequirement> requirements) =>
            new(true, true, string.Empty, string.Empty, string.Empty, requirements);

        public static RuntimeActionAuthorizationDecision InvalidDefinition(string reason) =>
            new(false, false, string.Empty, string.Empty, reason, []);

        public static RuntimeActionAuthorizationDecision PermissionDenied(
            string permissionAction,
            string target) =>
            new(false, true, permissionAction, target, "Action permission denied.", []);
    }

    private sealed record RuntimeActionAuthorizationRequirement(
        string Action,
        RuntimeEntityDefinition Entity);
}
