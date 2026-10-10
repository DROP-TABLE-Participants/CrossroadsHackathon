using System.Text.Json;
using ErpGenerator.Runtime.Shared.Bundles;

namespace ErpGenerator.Runtime.Shared.Compilation;

public sealed class ErpBundleMigrationPlanner
{
    public BundleMigrationPlan PlanMigration(CompiledBundle? from, CompiledBundle to)
    {
        var operations = new List<BundleMigrationOperation>();

        if (from is null)
        {
            operations.AddRange(to.Entities.Select(entity =>
                new BundleMigrationOperation("add_entity", "safe", Entity: entity.Id)));
            operations.AddRange(to.Views.Select(view =>
                new BundleMigrationOperation("add_view", "safe", Entity: view.Entity, Field: view.Id)));
            operations.AddRange(to.Reports.Select(report =>
                new BundleMigrationOperation("add_report", "safe", Entity: report.SourceEntity, Field: report.Key)));
            operations.AddRange(to.Dashboard.Widgets.Select(widget =>
                new BundleMigrationOperation("add_dashboard_widget", "safe", Field: widget.Key)));
            operations.AddRange(to.Workflows.Select(workflow =>
                new BundleMigrationOperation("add_workflow", "safe", Entity: workflow.Entity, Field: workflow.Id)));
            operations.AddRange((to.Schedules ?? []).Select(schedule =>
                new BundleMigrationOperation(
                    "add_schedule",
                    "warning",
                    Entity: schedule.Entity,
                    Field: schedule.Id,
                    Reason: $"Adding schedule '{schedule.Id}' enables autonomous workflow execution and requires explicit review.")));
            operations.AddRange((to.Actions ?? []).Select(action =>
                new BundleMigrationOperation("add_action", "safe", Entity: action.Entity, Field: action.Key)));
            operations.AddRange(to.Roles.SelectMany(role => role.Grants.Select(grant =>
                new BundleMigrationOperation(
                    "add_permission",
                    "safe",
                    Entity: grant.Entity,
                    Field: grant.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase)
                        ? $"{role.Id}:{grant.Action}"
                        : grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.OrdinalIgnoreCase)
                            ? RecordGrantKey(role.Id, grant)
                        : $"{role.Id}:{grant.Scope}:{grant.Action}:{grant.Target}"))));

            return BuildPlan(null, to.Version, operations);
        }

        DiffEntities(from, to, operations);
        DiffViews(from, to, operations);
        DiffReports(from, to, operations);
        DiffDashboard(from, to, operations);
        DiffWorkflows(from, to, operations);
        DiffSchedules(from, to, operations);
        DiffActions(from, to, operations);
        DiffPermissions(from, to, operations);

        return BuildPlan(from.Version, to.Version, operations);
    }

    private static BundleMigrationPlan BuildPlan(
        string? fromVersion,
        string toVersion,
        IReadOnlyList<BundleMigrationOperation> operations)
    {
        var blockedReasons = operations
            .Where(operation => operation.Risk.Equals("blocked", StringComparison.OrdinalIgnoreCase))
            .Select(operation => operation.Reason ?? operation.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var risk = blockedReasons.Length > 0
            ? "blocked"
            : operations.Any(operation => operation.Risk.Equals("warning", StringComparison.OrdinalIgnoreCase))
                ? "warning"
                : "safe";

        return new BundleMigrationPlan(fromVersion, toVersion, risk, operations, blockedReasons);
    }

    private static void DiffEntities(CompiledBundle from, CompiledBundle to, List<BundleMigrationOperation> operations)
    {
        var fromEntities = from.Entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        var toEntities = to.Entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var entity in to.Entities.Where(entity => !fromEntities.ContainsKey(entity.Id)))
        {
            operations.Add(new BundleMigrationOperation("add_entity", "safe", Entity: entity.Id));
        }

        foreach (var entity in from.Entities.Where(entity => !toEntities.ContainsKey(entity.Id)))
        {
            operations.Add(new BundleMigrationOperation(
                "blocked_drop_entity",
                "blocked",
                Entity: entity.Id,
                Rollbackable: false,
                Reason: $"Dropping entity '{entity.Id}' is destructive and requires an explicit migration mapping."));
        }

        foreach (var entity in to.Entities.Where(entity => fromEntities.ContainsKey(entity.Id)))
        {
            var previous = fromEntities[entity.Id];
            if (!previous.TableName.Equals(entity.TableName, StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(new BundleMigrationOperation(
                    "blocked_drop_entity",
                    "blocked",
                    Entity: entity.Id,
                    From: previous.TableName,
                    To: entity.TableName,
                    Rollbackable: false,
                    Reason: $"Renaming entity '{entity.Id}' or changing its table requires an explicit migration mapping."));
            }

            if (!previous.ModuleId.Equals(entity.ModuleId, StringComparison.Ordinal) ||
                !previous.Name.Equals(entity.Name, StringComparison.Ordinal) ||
                !previous.DisplayName.Equals(entity.DisplayName, StringComparison.Ordinal) ||
                !JsonEquals(previous.Display, entity.Display))
            {
                operations.Add(new BundleMigrationOperation(
                    "update_entity_metadata",
                    "safe",
                    Entity: entity.Id,
                    Reason: $"Entity '{entity.Id}' display metadata changed and will be versioned."));
            }

            DiffFields(previous, entity, operations);
        }
    }

    private static void DiffFields(EntityDefinition from, EntityDefinition to, List<BundleMigrationOperation> operations)
    {
        var fromFields = from.Fields.ToDictionary(field => field.Id, StringComparer.OrdinalIgnoreCase);
        var toFields = to.Fields.ToDictionary(field => field.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var field in to.Fields.Where(field => !fromFields.ContainsKey(field.Id)))
        {
            if (field.Required && field.Computed is null)
            {
                operations.Add(new BundleMigrationOperation(
                    "blocked_required_field_without_default",
                    "blocked",
                    Entity: to.Id,
                    Field: field.Id,
                    Nullable: false,
                    Rollbackable: false,
                    Reason: $"Adding required field '{to.Id}.{field.Id}' without a default or backfill is unsafe."));
                continue;
            }

            var type = field.Computed is not null
                ? "add_computed_field"
                : field.Type.Equals("relation", StringComparison.OrdinalIgnoreCase)
                    ? "add_relation_field"
                    : "add_field";
            operations.Add(new BundleMigrationOperation(type, "safe", Entity: to.Id, Field: field.Id, Nullable: !field.Required));
        }

        foreach (var field in from.Fields.Where(field => !toFields.ContainsKey(field.Id)))
        {
            operations.Add(new BundleMigrationOperation(
                "deprecate_field",
                "warning",
                Entity: from.Id,
                Field: field.Id,
                Reason: $"Field '{from.Id}.{field.Id}' will be hidden from runtime metadata; the physical column is retained."));
        }

        foreach (var field in to.Fields.Where(field => fromFields.ContainsKey(field.Id)))
        {
            var previous = fromFields[field.Id];
            if (!previous.ColumnName.Equals(field.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(new BundleMigrationOperation(
                    "blocked_rename_field_column",
                    "blocked",
                    Entity: to.Id,
                    Field: field.Id,
                    From: previous.ColumnName,
                    To: field.ColumnName,
                    Rollbackable: false,
                    Reason: $"Renaming physical column '{to.Id}.{previous.ColumnName}' requires an explicit data migration."));
            }

            if (!previous.Type.Equals(field.Type, StringComparison.OrdinalIgnoreCase) ||
                !previous.SqlType.Equals(field.SqlType, StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(new BundleMigrationOperation(
                    "blocked_change_field_type",
                    "blocked",
                    Entity: to.Id,
                    Field: field.Id,
                    From: previous.Type,
                    To: field.Type,
                    Rollbackable: false,
                    Reason: $"Changing field type for '{to.Id}.{field.Id}' may corrupt existing data."));
            }

            if (!string.Equals(previous.References, field.References, StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(new BundleMigrationOperation(
                    "blocked_relation_target_change",
                    "blocked",
                    Entity: to.Id,
                    Field: field.Id,
                    From: previous.References,
                    To: field.References,
                    Rollbackable: false,
                    Reason: $"Changing relation target for '{to.Id}.{field.Id}' is unsafe without explicit mapping."));
            }

            if (!previous.Required && field.Required)
            {
                operations.Add(new BundleMigrationOperation(
                    "blocked_required_field_without_default",
                    "blocked",
                    Entity: to.Id,
                    Field: field.Id,
                    Nullable: false,
                    Rollbackable: false,
                    Reason: $"Making field '{to.Id}.{field.Id}' required requires a default or backfill."));
            }

            if (previous.Required && !field.Required)
            {
                operations.Add(new BundleMigrationOperation(
                    "blocked_make_field_optional_without_ddl",
                    "blocked",
                    Entity: to.Id,
                    Field: field.Id,
                    Nullable: true,
                    Rollbackable: false,
                    Reason: $"Making field '{to.Id}.{field.Id}' optional requires an explicit ALTER COLUMN migration."));
            }

            if (previous.Unique != field.Unique)
            {
                operations.Add(new BundleMigrationOperation(
                    "blocked_change_field_uniqueness",
                    "blocked",
                    Entity: to.Id,
                    Field: field.Id,
                    From: previous.Unique.ToString(),
                    To: field.Unique.ToString(),
                    Rollbackable: false,
                    Reason: $"Changing uniqueness for '{to.Id}.{field.Id}' requires an explicit index and data validation plan."));
            }

            if (!JsonEquals(previous.Computed, field.Computed))
            {
                operations.Add(new BundleMigrationOperation(
                    "update_computed_field",
                    "warning",
                    Entity: to.Id,
                    Field: field.Id,
                    Reason: $"Computed expression for '{to.Id}.{field.Id}' changed and requires business review."));
            }

            AddLifecycleMigrationOperations(to.Id, previous, field, operations);

            if (!previous.Name.Equals(field.Name, StringComparison.Ordinal) ||
                !previous.DisplayName.Equals(field.DisplayName, StringComparison.Ordinal))
            {
                operations.Add(new BundleMigrationOperation(
                    "update_field_metadata",
                    "safe",
                    Entity: to.Id,
                    Field: field.Id,
                    Reason: $"Field '{to.Id}.{field.Id}' display metadata changed and will be versioned."));
            }

            if (previous.Type.Equals("enum", StringComparison.OrdinalIgnoreCase) &&
                field.Type.Equals("enum", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var removed in previous.Options.Except(field.Options, StringComparer.OrdinalIgnoreCase))
                {
                    operations.Add(new BundleMigrationOperation(
                        "blocked_remove_enum_value",
                        "blocked",
                        Entity: to.Id,
                        Field: field.Id,
                        From: removed,
                        Rollbackable: false,
                        Reason: $"Removing enum value '{removed}' from '{to.Id}.{field.Id}' may strand existing data."));
                }

                foreach (var added in field.Options.Except(previous.Options, StringComparer.OrdinalIgnoreCase))
                {
                    operations.Add(new BundleMigrationOperation("add_field", "safe", Entity: to.Id, Field: field.Id, To: added));
                }
            }
        }
    }

    private static void AddLifecycleMigrationOperations(
        string entityId,
        FieldDefinition previous,
        FieldDefinition current,
        List<BundleMigrationOperation> operations)
    {
        if (JsonEquals(previous.Lifecycle, current.Lifecycle))
        {
            return;
        }

        var previousTransitions = previous.Lifecycle?.Transitions;
        var currentTransitions = current.Lifecycle?.Transitions;
        if (previousTransitions?.Equals("named_action", StringComparison.OrdinalIgnoreCase) == true &&
            currentTransitions?.Equals("named_action", StringComparison.OrdinalIgnoreCase) != true)
        {
            operations.Add(new BundleMigrationOperation(
                "review_lifecycle_privilege_widening",
                "warning",
                Entity: entityId,
                Field: current.Id,
                From: previousTransitions,
                To: currentTransitions,
                Reason: $"Changing named-action lifecycle field '{entityId}.{current.Id}' to direct or legacy transitions widens mutation authority and requires review."));
            return;
        }

        operations.Add(new BundleMigrationOperation(
            "update_field_lifecycle",
            "warning",
            Entity: entityId,
            Field: current.Id,
            From: FormatLifecycle(previous.Lifecycle),
            To: FormatLifecycle(current.Lifecycle),
            Reason: $"Lifecycle policy for '{entityId}.{current.Id}' changed and requires business review."));
    }

    private static string? FormatLifecycle(FieldLifecycleDefinition? lifecycle) =>
        lifecycle is null ? null : $"{lifecycle.Initial}:{lifecycle.Transitions}";

    private static void DiffViews(CompiledBundle from, CompiledBundle to, List<BundleMigrationOperation> operations)
    {
        DiffBehaviorByKey(
            from.Views,
            to.Views,
            view => view.Id,
            "view",
            operations);
    }

    private static void DiffReports(CompiledBundle from, CompiledBundle to, List<BundleMigrationOperation> operations)
    {
        DiffBehaviorByKey(
            from.Reports,
            to.Reports,
            report => report.Key,
            "report",
            operations);
    }

    private static void DiffDashboard(CompiledBundle from, CompiledBundle to, List<BundleMigrationOperation> operations)
    {
        DiffBehaviorByKey(
            from.Dashboard.Widgets,
            to.Dashboard.Widgets,
            widget => widget.Key,
            "dashboard_widget",
            operations);
    }

    private static void DiffWorkflows(CompiledBundle from, CompiledBundle to, List<BundleMigrationOperation> operations)
    {
        DiffBehaviorByKey(
            from.Workflows,
            to.Workflows,
            workflow => workflow.Id,
            "workflow",
            operations);
    }

    private static void DiffActions(CompiledBundle from, CompiledBundle to, List<BundleMigrationOperation> operations)
    {
        var fromActions = from.Actions ?? [];
        var toActions = to.Actions ?? [];
        DiffBehaviorByKey(
            fromActions,
            toActions,
            action => action.Key,
            "action",
            operations,
            action => action.Entity);
    }

    private static void DiffSchedules(
        CompiledBundle from,
        CompiledBundle to,
        List<BundleMigrationOperation> operations)
    {
        var fromSchedules = (from.Schedules ?? []).ToDictionary(schedule => schedule.Id, StringComparer.OrdinalIgnoreCase);
        var toSchedules = (to.Schedules ?? []).ToDictionary(schedule => schedule.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var id in toSchedules.Keys.Except(fromSchedules.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var schedule = toSchedules[id];
            operations.Add(new BundleMigrationOperation(
                "add_schedule",
                "warning",
                Entity: schedule.Entity,
                Field: schedule.Id,
                Reason: $"Adding schedule '{schedule.Id}' enables autonomous workflow execution and requires explicit review."));
        }

        foreach (var id in fromSchedules.Keys.Except(toSchedules.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var schedule = fromSchedules[id];
            operations.Add(new BundleMigrationOperation(
                "deprecate_schedule",
                "warning",
                Entity: schedule.Entity,
                Field: schedule.Id,
                Reason: $"Removing schedule '{schedule.Id}' disables autonomous workflow execution and requires explicit review."));
        }

        foreach (var id in toSchedules.Keys.Intersect(fromSchedules.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var previous = fromSchedules[id];
            var current = toSchedules[id];
            if (previous.DefinitionHash.Equals(current.DefinitionHash, StringComparison.Ordinal))
            {
                continue;
            }

            operations.Add(new BundleMigrationOperation(
                "update_schedule",
                "warning",
                Entity: current.Entity,
                Field: current.Id,
                From: previous.DefinitionHash,
                To: current.DefinitionHash,
                Reason: $"Schedule '{current.Id}' changed autonomous execution semantics and requires explicit review."));
        }
    }

    private static void DiffPermissions(CompiledBundle from, CompiledBundle to, List<BundleMigrationOperation> operations)
    {
        var fromGrants = GrantKeys(from).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toGrants = GrantKeys(to).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var grant in toGrants.Except(fromGrants, StringComparer.OrdinalIgnoreCase))
        {
            operations.Add(new BundleMigrationOperation("add_permission", "safe", Field: grant));
        }

        foreach (var grant in fromGrants.Except(toGrants, StringComparer.OrdinalIgnoreCase))
        {
            operations.Add(new BundleMigrationOperation(
                "blocked_remove_permission",
                "blocked",
                Field: grant,
                Rollbackable: false,
                Reason: $"Removing permission '{grant}' could lock users out and requires explicit approval."));
        }
    }

    private static IEnumerable<string> GrantKeys(CompiledBundle bundle) =>
        bundle.Roles.SelectMany(role => role.Grants.Select(grant =>
            grant.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase)
                ? $"{role.Id}:{grant.Action}:{grant.Entity}"
                : grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.OrdinalIgnoreCase)
                    ? RecordGrantKey(role.Id, grant)
                : $"{role.Id}:{grant.Scope}:{grant.Action}:{grant.Entity}:{grant.Target}"));

    private static string RecordGrantKey(string roleId, PermissionGrantDefinition grant)
    {
        var scope = grant.RecordScope
                    ?? throw new InvalidOperationException(
                        $"Compiled record grant '{roleId}:{grant.Action}:{grant.Entity}' is missing its typed scope.");
        return string.Join(
            ':',
            roleId,
            PermissionGrantScopes.Record,
            grant.Action,
            grant.Entity,
            scope.Field,
            scope.Source,
            scope.LinkedEntity ?? "-",
            scope.LinkedField ?? "-");
    }

    private static void AddAndDeprecateByKey(
        IEnumerable<string> fromKeys,
        IEnumerable<string> toKeys,
        Action<string> add,
        Action<string> deprecate)
    {
        var fromSet = fromKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toSet = toKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in toSet.Except(fromSet, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            add(id);
        }

        foreach (var id in fromSet.Except(toSet, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            deprecate(id);
        }
    }

    private static void DiffBehaviorByKey<T>(
        IReadOnlyList<T> from,
        IReadOnlyList<T> to,
        Func<T, string> key,
        string kind,
        List<BundleMigrationOperation> operations,
        Func<T, string?>? entity = null)
    {
        var fromByKey = from.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        var toByKey = to.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        AddAndDeprecateByKey(
            fromByKey.Keys,
            toByKey.Keys,
            id =>
            {
                var added = toByKey[id];
                operations.Add(new BundleMigrationOperation(
                    $"add_{kind}",
                    "safe",
                    Entity: entity?.Invoke(added),
                    Field: id));
            },
            id => operations.Add(new BundleMigrationOperation($"deprecate_{kind}", "warning", Field: id)));

        foreach (var id in toByKey.Keys.Intersect(fromByKey.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var previous = fromByKey[id];
            var current = toByKey[id];
            if (JsonEquals(previous, current))
            {
                continue;
            }

            operations.Add(new BundleMigrationOperation(
                $"update_{kind}",
                "warning",
                Entity: entity?.Invoke(current),
                Field: id,
                Reason: $"Existing {kind.Replace('_', ' ')} '{id}' changed semantics and requires explicit review."));
        }
    }

    private static bool JsonEquals<T>(T left, T right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
