using ErpGenerator.Runtime.Shared.Bundles;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleValidator
{
    public static IReadOnlyList<string> Validate(ErpBundle bundle)
    {
        var context = new BundleValidationContext(bundle);
        var errors = context.Errors;

        RequireIdentifier(bundle.Id, "bundle.id", errors);
        RequireText(bundle.Name, "bundle.name", errors);
        RequireText(bundle.Version, "bundle.version", errors);
        RejectSqlLikeContent(bundle.Name, "bundle.name", errors);
        RejectSqlLikeContent(bundle.Description, "bundle.description", errors);

        if (bundle.Modules.Count == 0)
        {
            errors.Add("bundle.modules must contain at least one module.");
        }

        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in bundle.Modules)
        {
            RequireIdentifier(module.Id, $"module[{module.Name}].id", errors);
            RequireText(module.Name, $"module[{module.Id}].name", errors);
            RejectSqlLikeContent(module.Name, $"module[{module.Id}].name", errors);

            if (!moduleIds.Add(module.Id))
            {
                errors.Add($"Duplicate module id '{module.Id}'.");
            }

            foreach (var entity in module.Entities)
            {
                BundleEntityValidator.ValidateEntity(entity, context.EntityIds, context.FieldsByEntity, errors);
            }
        }

        BundleEntityValidator.ValidateIdentityNamespace(
            bundle.Modules.SelectMany(module => module.Entities),
            errors);

        foreach (var duplicate in bundle.Modules
                     .SelectMany(module => module.Workflows)
                     .Where(workflow =>
                         !string.IsNullOrWhiteSpace(workflow.Entity) &&
                         !string.IsNullOrWhiteSpace(workflow.Trigger.Action))
                     .GroupBy(
                         workflow => $"{workflow.Entity}\u001f{workflow.Trigger.Action}",
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(
                $"Workflows '{string.Join("', '", duplicate.Select(workflow => workflow.Id).Order(StringComparer.OrdinalIgnoreCase))}' share entity '{duplicate.First().Entity}' and trigger '{duplicate.First().Trigger.Action}'; workflow start identity must be unique.");
        }

        foreach (var module in bundle.Modules)
        {
            foreach (var entity in module.Entities)
            {
                BundleEntityValidator.ValidateRelationTargets(entity, context.EntityIds, errors);
                BundleEntityValidator.ValidateRelationCreateGuards(entity, context.FieldsByEntity, errors);
                BundleEntityValidator.ValidateCollections(
                    entity,
                    context.FieldsByEntity,
                    bundle.Actions.Select(action => action.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    errors);
            }

            foreach (var view in module.Views)
            {
                RequireIdentifier(view.Id, $"view[{view.Name}].id", errors);
                RequireText(view.Name, $"view[{view.Id}].name", errors);
                RejectSqlLikeContent(view.Name, $"view[{view.Id}].name", errors);

                if (!context.EntityIds.Contains(view.Entity))
                {
                    errors.Add($"View '{view.Id}' references unknown entity '{view.Entity}'.");
                }
            }

            foreach (var workflow in module.Workflows)
            {
                RequireIdentifier(workflow.Id, $"workflow[{workflow.Name}].id", errors);
                RequireText(workflow.Name, $"workflow[{workflow.Id}].name", errors);
                RejectSqlLikeContent(workflow.Name, $"workflow[{workflow.Id}].name", errors);

                if (!context.EntityIds.Contains(workflow.Entity))
                {
                    errors.Add($"Workflow '{workflow.Id}' references unknown entity '{workflow.Entity}'.");
                }

                BundleWorkflowValidator.ValidateWorkflow(
                    workflow,
                    context.FieldsByEntity,
                    context.RoleIds,
                    bundle.Roles,
                    errors);
            }
        }

        BundlePermissionValidator.ValidateRoles(bundle, context.EntityIds, errors);
        BundleReportValidator.ValidateReports(bundle, context.FieldsByEntity, errors);
        BundleDashboardValidator.ValidateDashboard(bundle, errors);
        BundleActionValidator.ValidateActions(bundle, context.FieldsByEntity, errors);
        BundleScheduleValidator.ValidateSchedules(bundle, context.FieldsByEntity, errors);
        BundleExceptionRuleValidator.Validate(bundle, context);
        BundleLifecycleValidator.ValidateFunctionalClosure(bundle, context.FieldsByEntity, errors);

        return errors;
    }
}
