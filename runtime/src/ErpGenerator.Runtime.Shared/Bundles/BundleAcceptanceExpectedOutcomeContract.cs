namespace ErpGenerator.Runtime.Shared.Bundles;

/// <summary>
/// Defines the closed v1 result taxonomy for generated acceptance-test steps.
/// It validates only deterministic contract facts; runtime exception/result
/// classification is intentionally a separate execution concern.
/// </summary>
public static class BundleAcceptanceExpectedOutcomeContract
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> PermissionCodesByStep =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["create_record"] = Codes(
                BundleAcceptanceExpectedOutcomeCodes.PermissionEntityCreate,
                BundleAcceptanceExpectedOutcomeCodes.PermissionFieldWrite,
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordCreate),
            ["update_record"] = Codes(
                BundleAcceptanceExpectedOutcomeCodes.PermissionEntityUpdate,
                BundleAcceptanceExpectedOutcomeCodes.PermissionFieldWrite,
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordUpdate),
            ["run_action"] = Codes(
                BundleAcceptanceExpectedOutcomeCodes.PermissionEntityCreate,
                BundleAcceptanceExpectedOutcomeCodes.PermissionEntityRead,
                BundleAcceptanceExpectedOutcomeCodes.PermissionEntityUpdate,
                BundleAcceptanceExpectedOutcomeCodes.PermissionEntityArchive,
                BundleAcceptanceExpectedOutcomeCodes.PermissionEntityRestore,
                BundleAcceptanceExpectedOutcomeCodes.PermissionFieldWrite,
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordRead,
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordUpdate,
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordArchive,
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordRestore,
                BundleAcceptanceExpectedOutcomeCodes.PermissionNamedActionExecute),
            ["start_workflow_action"] = Codes(
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordStartWorkflow,
                BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowStart),
            ["approve_task"] = Codes(
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordApproveTask,
                BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment),
            ["reject_task"] = Codes(
                BundleAcceptanceExpectedOutcomeCodes.PermissionRecordRejectTask,
                BundleAcceptanceExpectedOutcomeCodes.PermissionWorkflowTaskAssignment),
            ["call_report"] = Codes(BundleAcceptanceExpectedOutcomeCodes.PermissionReportExecute)
        };

    public static string? FindError(BundleAcceptanceStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Expected is null)
        {
            return $"Acceptance step '{step.Id}' must declare a non-null expected outcome contract.";
        }

        var type = step.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        var outcome = step.Expected.Outcome ?? string.Empty;
        var code = step.Expected.Code ?? string.Empty;
        var target = step.Expected.Target ?? string.Empty;
        if (!BundleAcceptanceExpectedOutcomes.All.Contains(outcome, StringComparer.Ordinal))
        {
            return $"Acceptance step '{step.Id}' uses unknown expected outcome '{outcome}'.";
        }

        if (outcome == BundleAcceptanceExpectedOutcomes.Success)
        {
            return code.Length == 0 && target.Length == 0
                ? null
                : $"Acceptance step '{step.Id}' expected success must use empty code and target.";
        }

        if (code.Length == 0 || target.Length == 0)
        {
            return $"Acceptance step '{step.Id}' expected outcome '{outcome}' requires a stable code and target.";
        }

        if (!IsStableTarget(target))
        {
            return $"Acceptance step '{step.Id}' expected target '{target}' is not a stable identifier.";
        }

        var combinationError = outcome switch
        {
            BundleAcceptanceExpectedOutcomes.PermissionDenied =>
                !PermissionCodesByStep.TryGetValue(type, out var allowedPermissionCodes) ||
                !allowedPermissionCodes.Contains(code)
                    ? $"Acceptance step '{step.Id}' does not support expected permission code '{code}' for step type '{type}'."
                    : null,
            BundleAcceptanceExpectedOutcomes.ActionConflict =>
                (type is "run_action" or "start_workflow_action") &&
                code == BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable
                    ? null
                    : $"Acceptance step '{step.Id}' does not support expected action conflict code '{code}' for step type '{type}'.",
            BundleAcceptanceExpectedOutcomes.BusinessRuleRejected =>
                type == "run_action" && code == BundleAcceptanceExpectedOutcomeCodes.ActionGuard ||
                type == "start_workflow_action" && code == BundleAcceptanceExpectedOutcomeCodes.WorkflowStartGuard ||
                type == "create_record" && code == BundleAcceptanceExpectedOutcomeCodes.RelationCreateGuard ||
                type == "update_record" && code == BundleAcceptanceExpectedOutcomeCodes.LifecycleNamedActionRequired
                    ? null
                    : $"Acceptance step '{step.Id}' does not support expected business-rule code '{code}' for step type '{type}'.",
            BundleAcceptanceExpectedOutcomes.WorkflowRejected =>
                type == "reject_task" && code == BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected
                    ? null
                    : $"Acceptance step '{step.Id}' does not support expected workflow-rejection code '{code}' for step type '{type}'.",
            _ => $"Acceptance step '{step.Id}' uses unsupported expected outcome '{outcome}'."
        };
        return combinationError ?? FindTargetError(step, code, target);
    }

    public static bool IsSuccess(BundleAcceptanceStep step) =>
        step.Expected is not null &&
        string.Equals(
            step.Expected.Outcome,
            BundleAcceptanceExpectedOutcomes.Success,
            StringComparison.Ordinal);

    public static bool IsRecordProducer(BundleAcceptanceStep step) =>
        string.Equals(step.Type, BundleAcceptanceStepTypes.ReferenceSeedRecord, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(step.Type, BundleAcceptanceStepTypes.CreateRecord, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(step.Type, BundleAcceptanceStepTypes.RunAction, StringComparison.OrdinalIgnoreCase);

    private static string? FindTargetError(BundleAcceptanceStep step, string code, string target)
    {
        var exactTarget = code switch
        {
            BundleAcceptanceExpectedOutcomeCodes.PermissionEntityCreate or
            BundleAcceptanceExpectedOutcomeCodes.PermissionEntityRead or
            BundleAcceptanceExpectedOutcomeCodes.PermissionEntityUpdate or
            BundleAcceptanceExpectedOutcomeCodes.PermissionEntityArchive or
            BundleAcceptanceExpectedOutcomeCodes.PermissionEntityRestore
                when !string.Equals(step.Type, "run_action", StringComparison.OrdinalIgnoreCase) => step.Entity,
            BundleAcceptanceExpectedOutcomeCodes.PermissionRecordCreate => step.Entity,
            BundleAcceptanceExpectedOutcomeCodes.PermissionNamedActionExecute or
            BundleAcceptanceExpectedOutcomeCodes.ActionNotAvailable => step.Action,
            BundleAcceptanceExpectedOutcomeCodes.PermissionReportExecute => step.Report,
            _ => null
        };
        if (exactTarget is not null && !string.Equals(target, exactTarget, StringComparison.Ordinal))
        {
            return $"Acceptance step '{step.Id}' expected code '{code}' must target exact id '{exactTarget}'.";
        }

        if ((code is BundleAcceptanceExpectedOutcomeCodes.PermissionFieldWrite or
             BundleAcceptanceExpectedOutcomeCodes.LifecycleNamedActionRequired) &&
            (string.Equals(step.Type, "create_record", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(step.Type, "update_record", StringComparison.OrdinalIgnoreCase)))
        {
            var fields = string.Equals(step.Type, "create_record", StringComparison.OrdinalIgnoreCase)
                ? step.Values.Keys
                : step.Set.Keys;
            if (!fields.Contains(target, StringComparer.Ordinal))
            {
                return $"Acceptance step '{step.Id}' expected field or lifecycle denial must target an exact field written by the step.";
            }
        }

        if (code == BundleAcceptanceExpectedOutcomeCodes.RelationCreateGuard &&
            !step.Values.ContainsKey(target))
        {
            return $"Acceptance step '{step.Id}' expected relation.create_guard must target an exact relation field written by the create step.";
        }

        return null;
    }

    private static IReadOnlySet<string> Codes(params string[] codes) =>
        new HashSet<string>(codes, StringComparer.Ordinal);

    private static bool IsStableTarget(string value) =>
        value.Length <= 128 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.');
}
