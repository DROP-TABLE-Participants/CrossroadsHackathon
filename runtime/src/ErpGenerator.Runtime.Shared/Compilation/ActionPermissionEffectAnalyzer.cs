namespace ErpGenerator.Runtime.Shared.Compilation;

public sealed record ActionPermissionEffectStep(
    string Id,
    string Type,
    string? Entity = null,
    string? ForEachResult = null,
    string? RecordsFromResult = null);

public sealed record ActionPermissionEffectRequirement(
    string StepId,
    string Action,
    string Entity);

public sealed record ActionPermissionEffectAnalysis(
    IReadOnlyList<ActionPermissionEffectRequirement> Requirements,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ActionPermissionEffectAnalyzer
{
    public const string EffectCreate = "effect_create";
    public const string EffectUpdate = "effect_update";

    public static string? EffectActionFor(string? directAction) =>
        directAction?.Trim().ToLowerInvariant() switch
        {
            "create" => EffectCreate,
            "update" => EffectUpdate,
            _ => null
        };

    public static string? DirectActionFor(string? mutationAction) =>
        mutationAction?.Trim().ToLowerInvariant() switch
        {
            "create" or EffectCreate => "create",
            "update" or EffectUpdate => "update",
            _ => null
        };

    public static bool IsMutationAction(string? action) =>
        DirectActionFor(action) is not null;

    public static bool IsExactDeclaredActionForRequirement(
        string? declaredAction,
        string? requiredAction)
    {
        if (string.IsNullOrWhiteSpace(declaredAction) || string.IsNullOrWhiteSpace(requiredAction))
        {
            return false;
        }

        var declared = declaredAction.Trim();
        var required = requiredAction.Trim();
        return declared.Equals(required, StringComparison.OrdinalIgnoreCase) ||
               EffectActionFor(required) is { } effect &&
               declared.Equals(effect, StringComparison.OrdinalIgnoreCase);
    }

    public static ActionPermissionEffectAnalysis Analyze(
        string? sourceEntity,
        IReadOnlyList<ActionPermissionEffectStep>? steps)
    {
        var requirements = new List<ActionPermissionEffectRequirement>();
        var errors = new List<string>();
        var seenStepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queryResultEntities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(sourceEntity))
        {
            errors.Add("Action permission effects cannot be derived without a source entity.");
        }

        if (steps is null)
        {
            errors.Add("Action permission effects cannot be derived without action steps.");
            return new ActionPermissionEffectAnalysis(requirements, errors);
        }

        foreach (var step in steps)
        {
            if (step is null)
            {
                errors.Add("Action permission effects cannot be derived for a null step.");
                continue;
            }

            var stepId = step.Id?.Trim() ?? string.Empty;
            var type = step.Type?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stepId))
            {
                errors.Add("Action permission effects cannot be derived for a step without an id.");
                continue;
            }

            if (!seenStepIds.Add(stepId))
            {
                errors.Add($"Action permission effects cannot be derived because step id '{stepId}' is duplicated.");
                continue;
            }

            switch (type)
            {
                case "query_records":
                    if (RequireEntity(stepId, type, step.Entity, errors) is { } queryEntity)
                    {
                        requirements.Add(new ActionPermissionEffectRequirement(stepId, "read", queryEntity));
                        queryResultEntities[stepId] = queryEntity;
                    }

                    break;
                case "create_record":
                    if (RequireEntity(stepId, type, step.Entity, errors) is { } createEntity)
                    {
                        requirements.Add(new ActionPermissionEffectRequirement(stepId, "create", createEntity));
                    }

                    break;
                case "create_records_for_each":
                    if (RequireEntity(stepId, type, step.Entity, errors) is { } createManyEntity)
                    {
                        requirements.Add(new ActionPermissionEffectRequirement(stepId, "create", createManyEntity));
                    }

                    RequireQueryResult(stepId, type, step.ForEachResult, queryResultEntities, errors);
                    break;
                case "update_records":
                    if (RequireQueryResult(stepId, type, step.RecordsFromResult, queryResultEntities, errors) is { } updateEntity)
                    {
                        requirements.Add(new ActionPermissionEffectRequirement(stepId, "update", updateEntity));
                    }

                    break;
                case "update_current_record":
                    if (!string.IsNullOrWhiteSpace(sourceEntity))
                    {
                        requirements.Add(new ActionPermissionEffectRequirement(stepId, "update", sourceEntity.Trim()));
                    }

                    break;
                case "guard":
                case "audit_event":
                case "return_result":
                    break;
                default:
                    errors.Add($"Action permission effects cannot be derived for step '{stepId}' with unsupported type '{step.Type}'.");
                    break;
            }
        }

        return new ActionPermissionEffectAnalysis(requirements, errors);
    }

    public static bool ActionMatches(string? declaredAction, string? requiredAction) =>
        !string.IsNullOrWhiteSpace(declaredAction) &&
        !string.IsNullOrWhiteSpace(requiredAction) &&
        (declaredAction.Trim().Equals("*", StringComparison.Ordinal) ||
         declaredAction.Trim().Equals(requiredAction.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool EntityMatches(string? declaredEntity, string? requiredEntity) =>
        !string.IsNullOrWhiteSpace(declaredEntity) &&
        !string.IsNullOrWhiteSpace(requiredEntity) &&
        (declaredEntity.Trim().Equals("*", StringComparison.Ordinal) ||
         declaredEntity.Trim().Equals(requiredEntity.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string? RequireEntity(
        string stepId,
        string stepType,
        string? entity,
        List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(entity))
        {
            return entity.Trim();
        }

        errors.Add($"Action permission effects cannot be derived because {stepType} step '{stepId}' has no entity.");
        return null;
    }

    private static string? RequireQueryResult(
        string stepId,
        string stepType,
        string? result,
        IReadOnlyDictionary<string, string> queryResultEntities,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            errors.Add($"Action permission effects cannot be derived because {stepType} step '{stepId}' has no query result reference.");
            return null;
        }

        if (queryResultEntities.TryGetValue(result.Trim(), out var entity))
        {
            return entity;
        }

        errors.Add($"Action permission effects cannot be derived because {stepType} step '{stepId}' references unknown query result '{result}'.");
        return null;
    }
}
