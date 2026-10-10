using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation.Hashing;
using ErpGenerator.Runtime.Shared.Compilation.Validation;
using System.Globalization;
using System.Text.Json;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;

namespace ErpGenerator.Runtime.Shared.Compilation.Metadata;

internal sealed record BundleMetadataCompilationResult(
    IReadOnlyList<EntityDefinition> Entities,
    IReadOnlyList<ViewDefinition> Views,
    IReadOnlyList<WorkflowDefinition> Workflows,
    IReadOnlyList<ScheduleDefinition> Schedules,
    IReadOnlyList<ActionDefinition> Actions,
    IReadOnlyList<RoleDefinition> Roles,
    IReadOnlyList<ReportDefinition> Reports,
    IReadOnlyList<ExceptionRuleDefinition> ExceptionRules,
    DashboardDefinition Dashboard);

internal static class BundleMetadataCompiler
{
    public static BundleMetadataCompilationResult Compile(ErpBundle bundle)
    {
        var entities = bundle.Modules
            .SelectMany(module => module.Entities.Select(entity => CompileEntity(module.Id, entity)))
            .OrderBy(entity => entity.Id, StringComparer.Ordinal)
            .ToArray();

        var views = bundle.Modules
            .SelectMany(module => module.Views.Select(view =>
                new ViewDefinition(view.Id, module.Id, view.Name, view.Entity, view.Fields)))
            .OrderBy(view => view.Id, StringComparer.Ordinal)
            .ToArray();

        var workflows = bundle.Modules
            .SelectMany(module => module.Workflows.Select(workflow =>
                new WorkflowDefinition(
                    workflow.Id,
                    module.Id,
                    workflow.Name,
                    workflow.Entity,
                    workflow.States,
                    string.IsNullOrWhiteSpace(workflow.Trigger.Action)
                        ? null
                        : new WorkflowTriggerDefinition(workflow.Trigger.Action.Trim()),
                    CompileWorkflowSteps(workflow.Steps),
                    CompileWorkflowSteps(workflow.OnRejected))
                {
                    StartGuards = workflow.StartGuards.Select(guard =>
                        new WorkflowStartGuardDefinition(
                            guard.Id.Trim(),
                            ResolveActionEntityId(guard.Entity, entities),
                            guard.Where.Select(CompileActionFilter).ToArray(),
                            guard.ResultCountOp.Trim().ToLowerInvariant(),
                            guard.ResultCount,
                            "Workflow start requirements are not satisfied."))
                        .ToArray()
                }))
            .OrderBy(workflow => workflow.Id, StringComparer.Ordinal)
            .ToArray();

        var schedules = CompileSchedules(bundle, entities, workflows);

        var actions = CompileActions(bundle, entities)
            .OrderBy(action => action.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var roles = CompileRoles(bundle, entities)
            .OrderBy(role => role.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var reports = CompileReports(bundle, entities, roles)
            .OrderBy(report => report.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var exceptionRules = bundle.ExceptionRules
            .Select(rule => new ExceptionRuleDefinition(
                rule.Id.Trim(),
                rule.Label.Trim(),
                rule.Report.Trim(),
                rule.Metric.Trim(),
                rule.Comparator.Trim().ToLowerInvariant(),
                rule.Threshold,
                rule.Severity.Trim().ToLowerInvariant(),
                rule.Schedule.Trim(),
                rule.RecipientRoles
                    .Select(NormalizeRoleId)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                rule.DeduplicationKey.Trim(),
                string.IsNullOrWhiteSpace(rule.ResolutionWorkflow)
                    ? null
                    : rule.ResolutionWorkflow.Trim()))
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();

        var dashboard = CompileDashboard(bundle);

        return new BundleMetadataCompilationResult(
            entities,
            views,
            workflows,
            schedules,
            actions,
            roles,
            reports,
            exceptionRules,
            dashboard);
    }

    private static IReadOnlyList<ScheduleDefinition> CompileSchedules(
        ErpBundle bundle,
        IReadOnlyList<EntityDefinition> entities,
        IReadOnlyList<WorkflowDefinition> workflows) =>
        bundle.Schedules
            .Select(schedule =>
            {
                var entity = entities.Single(candidate =>
                    candidate.Id.Equals(schedule.Entity, StringComparison.Ordinal));
                var dueField = entity.Fields.Single(field =>
                    field.Id.Equals(schedule.DueField, StringComparison.Ordinal));
                var workflow = workflows.Single(candidate =>
                    candidate.Id.Equals(schedule.Workflow, StringComparison.Ordinal));
                var filters = schedule.Filters
                    .Select(filter => CompileScheduleFilter(entity, filter))
                    .OrderBy(filter => filter.Field, StringComparer.Ordinal)
                    .ThenBy(filter => filter.Op, StringComparer.Ordinal)
                    .ThenBy(filter => ScheduleDefinitionHashCalculator.LiteralSortKey(filter.Value), StringComparer.Ordinal)
                    .ThenBy(filter => string.Join('\u001f', filter.Values.Select(ScheduleDefinitionHashCalculator.LiteralSortKey)), StringComparer.Ordinal)
                    .ToArray();
                var id = schedule.Id.Trim();
                var definitionHash = ScheduleDefinitionHashCalculator.Compute(
                    id,
                    entity.Id,
                    dueField.Id,
                    filters,
                    workflow.Id);
                return new ScheduleDefinition(
                    id,
                    entity.Id,
                    dueField.Id,
                    filters,
                    workflow.Id,
                    definitionHash);
            })
            .OrderBy(schedule => schedule.Id, StringComparer.Ordinal)
            .ToArray();

    private static ScheduleFilterDefinition CompileScheduleFilter(
        EntityDefinition entity,
        ErpScheduleFilter filter)
    {
        var field = entity.Fields.Single(candidate =>
            candidate.Id.Equals(filter.Field, StringComparison.Ordinal));
        return new ScheduleFilterDefinition(
            field.Id,
            filter.Op.Trim().ToLowerInvariant(),
            filter.Value is null ? null : CompileScheduleLiteral(filter.Value),
            filter.Values.Select(CompileScheduleLiteral)
                .OrderBy(ScheduleDefinitionHashCalculator.LiteralSortKey, StringComparer.Ordinal)
                .ToArray());
    }

    private static TypedLiteralDefinition CompileScheduleLiteral(ErpTypedLiteral literal)
    {
        var type = literal.Type.Trim().ToLowerInvariant();
        var value = literal.Value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement element => element.GetRawText(),
            _ => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? string.Empty
        };
        var canonicalValue = type switch
        {
            "integer" => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture),
            "decimal" => decimal.Parse(
                    value,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture)
                .ToString("0.#############################", CultureInfo.InvariantCulture),
            "boolean" => bool.Parse(value).ToString().ToLowerInvariant(),
            "date" => DateOnly.Parse(value, CultureInfo.InvariantCulture)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "datetime" => DateTimeOffset.Parse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                .ToString("O", CultureInfo.InvariantCulture),
            _ => value
        };
        return new TypedLiteralDefinition(type, canonicalValue);
    }

    static EntityDefinition CompileEntity(string moduleId, ErpEntity entity)
    {
        var fields = entity.Fields
            .Select(field => new FieldDefinition(
                field.Id,
                field.Name,
                field.DisplayName,
                field.Type,
                field.Name,
                ToSqlType(field),
                field.Required,
                field.Unique,
                field.References,
                field.Options,
                CompileComputedField(field.Computed),
                CompileLifecycle(field)))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();

        var display = ResolveDisplay(entity, fields);
        return new EntityDefinition(entity.Id, moduleId, entity.Name, entity.DisplayName, entity.Name, display, fields)
        {
            Relationships = entity.Fields
                .Where(field =>
                    field.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(field.References))
                .Select(field => new RelationshipDefinition(
                    string.IsNullOrWhiteSpace(field.Relationship?.Key)
                        ? $"{entity.Id}_{field.Id}"
                        : field.Relationship.Key.Trim(),
                    entity.Id,
                    field.Id,
                    field.References!,
                    field.Required,
                    string.IsNullOrWhiteSpace(field.Relationship?.Cardinality)
                        ? "many_to_one"
                        : field.Relationship.Cardinality.Trim().ToLowerInvariant(),
                    string.IsNullOrWhiteSpace(field.Relationship?.Inverse)
                        ? null
                        : field.Relationship.Inverse.Trim(),
                    string.IsNullOrWhiteSpace(field.Relationship?.ArchiveBehavior)
                        ? "restrict"
                        : field.Relationship.ArchiveBehavior.Trim().ToLowerInvariant()))
                .OrderBy(relationship => relationship.Key, StringComparer.Ordinal)
                .ToArray(),
            Collections = entity.Collections
                .Select(collection => new EntityCollectionDefinition(
                    collection.Key.Trim(),
                    collection.Kind.Trim().ToLowerInvariant(),
                    collection.Label.Trim(),
                    collection.ChildEntity.Trim(),
                    collection.ParentRelation.Trim(),
                    string.IsNullOrWhiteSpace(collection.OrderField)
                        ? null
                        : collection.OrderField.Trim(),
                    collection.ShownFields.ToArray(),
                    collection.EditableFields.ToArray(),
                    collection.ActionKeys.ToArray()))
                .OrderBy(collection => collection.Key, StringComparer.Ordinal)
                .ToArray(),
            RelationCreateGuards = entity.RelationCreateGuards.Select(guard =>
            {
                var relation = fields.Single(field =>
                    field.Id.Equals(guard.RelationField, StringComparison.OrdinalIgnoreCase));
                var literal = Convert.ToString(guard.Value.Value, System.Globalization.CultureInfo.InvariantCulture)!;
                return new RelationCreateGuardDefinition(
                    relation.Id,
                    relation.References!,
                    guard.ReferencedField,
                    guard.Op,
                    new TypedLiteralDefinition(guard.Value.Type, literal));
            }).ToArray()
        };
    }

    static ComputedFieldDefinition? CompileComputedField(ErpComputedField? computed) =>
        computed is null || string.IsNullOrWhiteSpace(computed.Op)
            ? null
            : new ComputedFieldDefinition(
                computed.Op.Trim().ToLowerInvariant(),
                CompileComputedOperand(computed.Left),
                CompileComputedOperand(computed.Right));

    static FieldLifecycleDefinition? CompileLifecycle(ErpField field)
    {
        if (field.Lifecycle is null)
        {
            return null;
        }

        var initial = field.Options.First(option =>
            option.Equals(field.Lifecycle.Initial.Trim(), StringComparison.OrdinalIgnoreCase));
        return new FieldLifecycleDefinition(
            initial,
            field.Lifecycle.Transitions.Trim().ToLowerInvariant());
    }

    static ComputedOperandDefinition CompileComputedOperand(ErpComputedOperand? operand) =>
        new(
            string.IsNullOrWhiteSpace(operand?.Field) ? null : operand.Field.Trim(),
            operand?.Value);

    static IReadOnlyList<RoleDefinition> CompileRoles(
        ErpBundle bundle,
        IReadOnlyList<EntityDefinition> entities)
    {
        var roles = EffectiveRoles(bundle);
        return roles.Select(role => new RoleDefinition(
                NormalizeRoleId(role.Key),
                string.IsNullOrWhiteSpace(role.Value.DisplayName) ? ToTitleCase(role.Key) : role.Value.DisplayName.Trim(),
                role.Value.Grants
                    .Select(grant => new PermissionGrantDefinition(
                        NormalizePermissionValue(grant.Action),
                        NormalizePermissionValue(grant.Entity),
                        string.IsNullOrWhiteSpace(grant.Scope)
                            ? PermissionGrantScopes.Entity
                            : NormalizePermissionValue(grant.Scope),
                        string.IsNullOrWhiteSpace(grant.Target)
                            ? string.Empty
                            : NormalizePermissionValue(grant.Target),
                        CompileRecordScope(grant, entities)))
                    .ToArray()))
            .ToArray();
    }

    static RecordScopeDefinition? CompileRecordScope(
        ErpGrant grant,
        IReadOnlyList<EntityDefinition> entities)
    {
        if (grant.RecordScope is null)
        {
            return null;
        }

        var entity = entities.Single(candidate =>
            candidate.Id.Equals(grant.Entity, StringComparison.OrdinalIgnoreCase));
        var field = string.Equals(grant.RecordScope.Field, "created_by", StringComparison.OrdinalIgnoreCase)
            ? "created_by"
            : entity.Fields.Single(candidate =>
                candidate.Id.Equals(grant.RecordScope.Field, StringComparison.OrdinalIgnoreCase)).Id;
        var linkedEntity = string.IsNullOrWhiteSpace(grant.RecordScope.LinkedEntity)
            ? null
            : entities.Single(candidate =>
                candidate.Id.Equals(grant.RecordScope.LinkedEntity, StringComparison.OrdinalIgnoreCase)).Id;
        var linkedField = string.IsNullOrWhiteSpace(grant.RecordScope.LinkedField)
            ? null
            : entities.Single(candidate => candidate.Id.Equals(linkedEntity, StringComparison.Ordinal))
                .Fields.Single(candidate =>
                    candidate.Id.Equals(grant.RecordScope.LinkedField, StringComparison.OrdinalIgnoreCase)).Id;

        return new RecordScopeDefinition(
            field,
            NormalizePermissionValue(grant.RecordScope.Source),
            linkedEntity,
            linkedField);
    }

    static IReadOnlyList<ReportDefinition> CompileReports(
        ErpBundle bundle,
        IReadOnlyList<EntityDefinition> entities,
        IReadOnlyList<RoleDefinition> roles) =>
        bundle.Reports.Select(report =>
        {
            var source = ResolveEntity(report.Source, entities)
                         ?? throw new InvalidOperationException($"Report '{report.Key}' references unknown source '{report.Source}'.");
            var joins = CompileReportJoins(report, source, entities);
            return new ReportDefinition(
                report.Key.Trim(),
                report.Label.Trim(),
                string.IsNullOrWhiteSpace(report.Description) ? null : report.Description.Trim(),
                source.Id,
                report.GroupBy
                    .Select(group => new ReportGroupByDefinition(
                        group.Field.Trim(),
                        string.IsNullOrWhiteSpace(group.Label) ? ToTitleCase(group.Field.Replace("_", " ", StringComparison.Ordinal)) : group.Label.Trim())
                    {
                        IdentityEntityId = ResolveDirectReportIdentityEntityId(group.Field, source, entities),
                        EvidenceValueKind = ResolveReportEvidenceValueKind(
                            group.Field,
                            source,
                            joins,
                            entities,
                            roles)
                    })
                    .ToArray(),
                report.Metrics
                    .Select(metric => new ReportMetricDefinition(
                        metric.Key.Trim(),
                        string.IsNullOrWhiteSpace(metric.Label) ? ToTitleCase(metric.Key.Replace("_", " ", StringComparison.Ordinal)) : metric.Label.Trim(),
                        metric.Op.Trim().ToLowerInvariant(),
                        string.IsNullOrWhiteSpace(metric.Field) ? null : metric.Field.Trim(),
                        CompileReportFilter(metric.Filter))
                    {
                        EvidenceValueKind = metric.Op.Trim().ToLowerInvariant() is "min" or "max" &&
                                            !string.IsNullOrWhiteSpace(metric.Field)
                            ? ResolveReportEvidenceValueKind(
                                metric.Field,
                                source,
                                joins,
                                entities,
                                roles)
                            : null
                    })
                    .ToArray(),
                report.Filters.Select(CompileReportFilter).Where(filter => filter is not null).Cast<ReportFilterDefinition>().ToArray(),
                joins);
        }).ToArray();

    static string? ResolveReportEvidenceValueKind(
        string fieldReference,
        EntityDefinition source,
        IReadOnlyList<ReportJoinDefinition> joins,
        IReadOnlyList<EntityDefinition> entities,
        IReadOnlyList<RoleDefinition> roles)
    {
        var parts = fieldReference.Trim().Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        EntityDefinition? entity = source;
        if (parts.Length == 2)
        {
            var join = joins.SingleOrDefault(candidate =>
                candidate.Alias.Equals(parts[0], StringComparison.Ordinal));
            entity = join is null
                ? null
                : entities.Single(candidate => candidate.Id.Equals(join.TargetEntity, StringComparison.Ordinal));
        }

        if (entity is null)
        {
            return null;
        }

        var fieldKey = parts[^1];
        var field = entity.Fields.SingleOrDefault(candidate =>
            candidate.Id.Equals(fieldKey, StringComparison.OrdinalIgnoreCase) ||
            candidate.Name.Equals(fieldKey, StringComparison.OrdinalIgnoreCase) ||
            candidate.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
            candidate.Name[..^3].Equals(fieldKey, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return null;
        }

        var isActorPrincipal = roles.SelectMany(role => role.Grants).Any(grant =>
            grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.Ordinal) &&
            grant.Entity.Equals(entity.Id, StringComparison.Ordinal) &&
            grant.RecordScope is
            {
                Source: RecordPermissionScopeSources.ActorPrincipal
            } recordScope &&
            recordScope.Field.Equals(field.Id, StringComparison.Ordinal));
        return isActorPrincipal
            ? BundleAcceptanceEvidenceValueKinds.ActorPrincipal
            : null;
    }

    static string? ResolveDirectReportIdentityEntityId(
        string fieldReference,
        EntityDefinition source,
        IReadOnlyList<EntityDefinition> entities)
    {
        var fieldKey = fieldReference.Trim();
        if (fieldKey.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var field = source.Fields.FirstOrDefault(candidate =>
            candidate.Id.Equals(fieldKey, StringComparison.OrdinalIgnoreCase) ||
            candidate.Name.Equals(fieldKey, StringComparison.OrdinalIgnoreCase) ||
            candidate.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
            candidate.Name[..^3].Equals(fieldKey, StringComparison.OrdinalIgnoreCase));
        if (field is null ||
            field.Computed is not null ||
            !field.Type.Equals("relation", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(field.References))
        {
            return null;
        }

        return ResolveEntity(field.References, entities)?.Id;
    }

    static IReadOnlyList<ReportJoinDefinition> CompileReportJoins(
        ErpReport report,
        EntityDefinition source,
        IReadOnlyList<EntityDefinition> entities)
    {
        var contexts = new Dictionary<string, (EntityDefinition Entity, int Depth)>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = (source, 0)
        };
        var joins = new List<ReportJoinDefinition>();
        foreach (var join in report.Joins)
        {
            var fromAlias = join.From.Trim();
            var from = contexts[fromAlias];
            var relationKey = join.Relation.Trim();
            var relation = from.Entity.Fields.First(field =>
                field.Id.Equals(relationKey, StringComparison.OrdinalIgnoreCase) ||
                field.Name.Equals(relationKey, StringComparison.OrdinalIgnoreCase) ||
                (field.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
                 field.Name[..^3].Equals(relationKey, StringComparison.OrdinalIgnoreCase)));
            var target = ResolveEntity(relation.References!, entities)
                         ?? throw new InvalidOperationException(
                             $"Report '{report.Key}' join '{join.Alias}' references unknown target '{relation.References}'.");
            var depth = from.Depth + 1;
            var definition = new ReportJoinDefinition(
                join.Alias.Trim(),
                fromAlias,
                relation.Id,
                target.Id,
                join.Type.Trim().ToLowerInvariant(),
                depth);
            joins.Add(definition);
            contexts[definition.Alias] = (target, depth);
        }

        return joins;
    }

    static ReportFilterDefinition? CompileReportFilter(ErpReportFilter? filter) =>
        filter is null || string.IsNullOrWhiteSpace(filter.Op)
            ? null
            : new ReportFilterDefinition(
                filter.Field.Trim(),
                filter.Op.Trim().ToLowerInvariant(),
                filter.Value,
                filter.Values);

    static DashboardDefinition CompileDashboard(ErpBundle bundle) =>
        new(bundle.Dashboard.Widgets
            .Select(widget => new DashboardWidgetDefinition(
                widget.Key.Trim(),
                string.IsNullOrWhiteSpace(widget.Label) ? ToTitleCase(widget.Key.Replace("_", " ", StringComparison.Ordinal)) : widget.Label.Trim(),
                string.IsNullOrWhiteSpace(widget.Type) ? "metric" : widget.Type.Trim().ToLowerInvariant(),
                widget.Report.Trim(),
                widget.Metric.Trim()))
            .ToArray());

    static IReadOnlyList<ActionDefinition> CompileActions(
        ErpBundle bundle,
        IReadOnlyList<EntityDefinition> entities) =>
        bundle.Actions.Select(action => new ActionDefinition(
                action.Key.Trim(),
                string.IsNullOrWhiteSpace(action.Label) ? ToTitleCase(action.Key.Replace("_", " ", StringComparison.Ordinal)) : action.Label.Trim(),
                ResolveActionEntityId(action.Entity, entities),
                string.IsNullOrWhiteSpace(action.Type) ? "record_action" : action.Type.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(action.Description) ? null : action.Description.Trim(),
                action.VisibleWhen.Select(CompileActionCondition).Where(condition => condition is not null).Cast<ActionConditionDefinition>().ToArray(),
                action.Requires.Permissions
                    .Select(permission => new ActionPermissionRequirementDefinition(
                        NormalizePermissionValue(permission.Action),
                        ResolveActionEntityId(permission.Entity, entities)))
                    .ToArray(),
                CompileActionSteps(action.Steps, entities)))
            .ToArray();

    static IReadOnlyList<ActionStepDefinition> CompileActionSteps(
        IReadOnlyList<ErpActionStep> steps,
        IReadOnlyList<EntityDefinition> entities) =>
        steps.Select(step => new ActionStepDefinition(
                step.Id.Trim(),
                step.Type.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(step.Entity) ? null : ResolveActionEntityId(step.Entity, entities),
                step.Where.Select(CompileActionFilter)
                    .ToArray(),
                CompileActionCondition(step.Condition),
                string.IsNullOrWhiteSpace(step.Error) ? null : step.Error.Trim(),
                CompileActionValueDictionary(step.Values),
                step.ForEach is null || string.IsNullOrWhiteSpace(step.ForEach.Result)
                    ? null
                    : new ActionForEachDefinition(step.ForEach.Result.Trim()),
                step.RecordsFrom is null || string.IsNullOrWhiteSpace(step.RecordsFrom.Result)
                    ? null
                    : new ActionRecordsFromDefinition(step.RecordsFrom.Result.Trim()),
                CompileActionValueDictionary(step.Set),
                string.IsNullOrWhiteSpace(step.Event) ? null : step.Event.Trim(),
                string.IsNullOrWhiteSpace(step.Message) ? null : step.Message.Trim(),
                CompileActionValueDictionary(step.Metadata),
                step.Return is null
                    ? null
                    : new ActionReturnDefinition(
                        string.IsNullOrWhiteSpace(step.Return.Step) ? null : step.Return.Step.Trim(),
                        string.IsNullOrWhiteSpace(step.Return.Entity) ? null : step.Return.Entity.Trim(),
                        string.IsNullOrWhiteSpace(step.Return.Message) ? null : step.Return.Message.Trim())))
            .ToArray();

    static ActionFilterDefinition CompileActionFilter(ErpActionFilter filter) =>
        new(
            filter.Field.Trim(),
            filter.Op.Trim().ToLowerInvariant(),
            filter.Value,
            CompileActionValueFrom(filter.ValueFrom),
            filter.Values);

    static string ResolveActionEntityId(string entityKey, IReadOnlyList<EntityDefinition> entities)
    {
        if (entityKey.Trim().Equals("*", StringComparison.Ordinal))
        {
            return "*";
        }

        return ResolveEntity(entityKey, entities)?.Id
               ?? throw new InvalidOperationException($"Action metadata references unknown entity '{entityKey}'.");
    }

    static IReadOnlyDictionary<string, ActionValueDefinition> CompileActionValueDictionary(
        IReadOnlyDictionary<string, ErpActionValue> values) =>
        values.ToDictionary(
            pair => pair.Key.Trim(),
            pair => CompileActionValue(pair.Value),
            StringComparer.OrdinalIgnoreCase);

    static ActionValueDefinition CompileActionValue(ErpActionValue value) =>
        new(
            value.Value,
            CompileActionValueFrom(value.ValueFrom),
            string.IsNullOrWhiteSpace(value.Template) ? null : value.Template.Trim(),
            value.Aggregate is null
                ? null
                : new ActionAggregateDefinition(
                    value.Aggregate.Result.Trim(),
                    value.Aggregate.Op.Trim().ToLowerInvariant(),
                    CompileActionExpression(value.Aggregate.Expression)));

    static ActionExpressionDefinition? CompileActionExpression(ErpActionExpression? expression) =>
        expression is null
            ? null
            : new ActionExpressionDefinition(
                expression.Op.Trim().ToLowerInvariant(),
                CompileActionOperand(expression.Left),
                CompileActionOperand(expression.Right));

    static ActionConditionDefinition? CompileActionCondition(ErpActionCondition? condition) =>
        condition is null
            ? null
            : new ActionConditionDefinition(
                condition.Op.Trim().ToLowerInvariant(),
                CompileActionOperand(condition.Left),
                CompileActionOperand(condition.Right),
                condition.Conditions.Select(CompileActionCondition).Where(compiled => compiled is not null).Cast<ActionConditionDefinition>().ToArray());

    static ActionOperandDefinition? CompileActionOperand(ErpActionOperand? operand) =>
        operand is null
            ? null
            : new ActionOperandDefinition(
                string.IsNullOrWhiteSpace(operand.Field) ? null : operand.Field.Trim(),
                operand.Value,
                string.IsNullOrWhiteSpace(operand.ResultCount) ? null : operand.ResultCount.Trim(),
                CompileActionValueFrom(operand.ValueFrom));

    static ActionValueFromDefinition? CompileActionValueFrom(ErpActionValueFrom? valueFrom) =>
        valueFrom is null
            ? null
            : new ActionValueFromDefinition(
                string.IsNullOrWhiteSpace(valueFrom.Record) ? null : valueFrom.Record.Trim(),
                string.IsNullOrWhiteSpace(valueFrom.Item) ? null : valueFrom.Item.Trim(),
                string.IsNullOrWhiteSpace(valueFrom.Step) ? null : valueFrom.Step.Trim(),
                string.IsNullOrWhiteSpace(valueFrom.Field) ? null : valueFrom.Field.Trim(),
                string.IsNullOrWhiteSpace(valueFrom.Now) ? null : valueFrom.Now.Trim().ToLowerInvariant());

    static IReadOnlyList<WorkflowStepDefinition> CompileWorkflowSteps(IReadOnlyList<ErpWorkflowStep> steps) =>
        steps.Select(CompileWorkflowStep).ToArray();

    static WorkflowStepDefinition CompileWorkflowStep(ErpWorkflowStep step) =>
        new(
                step.Id.Trim(),
                step.Type.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(step.Label) ? ToTitleCase(step.Id.Replace("_", " ", StringComparison.Ordinal)) : step.Label.Trim(),
                CompileWorkflowAssignment(step.AssignedTo),
                string.IsNullOrWhiteSpace(step.ApproveLabel) ? null : step.ApproveLabel.Trim(),
                string.IsNullOrWhiteSpace(step.RejectLabel) ? null : step.RejectLabel.Trim(),
                step.Set.ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase),
                CompileWorkflowCondition(step.Condition ?? step.If),
                CompileWorkflowSteps(step.Then),
                string.IsNullOrWhiteSpace(step.Event) ? null : step.Event.Trim(),
                string.IsNullOrWhiteSpace(step.Message) ? null : step.Message.Trim(),
                step.ReviewFields.Select(field => field.Trim()).ToArray());

    static WorkflowConditionDefinition? CompileWorkflowCondition(ErpWorkflowCondition? condition) =>
        condition is null
            ? null
            : new WorkflowConditionDefinition(
                condition.Op.Trim().ToLowerInvariant(),
                CompileWorkflowConditionOperand(condition.Left),
                CompileWorkflowConditionOperand(condition.Right),
                condition.Conditions.Select(CompileWorkflowCondition).Where(compiled => compiled is not null).Cast<WorkflowConditionDefinition>().ToArray());

    static WorkflowConditionOperandDefinition? CompileWorkflowConditionOperand(ErpWorkflowConditionOperand? operand)
    {
        if (operand is null)
        {
            return null;
        }

        return new WorkflowConditionOperandDefinition(
            string.IsNullOrWhiteSpace(operand.Field) ? null : operand.Field.Trim(),
            string.IsNullOrWhiteSpace(operand.Path) ? null : operand.Path.Trim(),
            operand.Value);
    }

    static WorkflowAssignmentDefinition? CompileWorkflowAssignment(ErpWorkflowAssignment assignment)
    {
        var type = string.IsNullOrWhiteSpace(assignment.Type)
            ? (string.IsNullOrWhiteSpace(assignment.Path) ? "role" : "relation_user")
            : assignment.Type.Trim().ToLowerInvariant();
        var role = string.IsNullOrWhiteSpace(assignment.Role) ? null : NormalizeRoleId(assignment.Role);
        var path = string.IsNullOrWhiteSpace(assignment.Path) ? null : assignment.Path.Trim();
        var requiredRole = string.IsNullOrWhiteSpace(assignment.RequiredRole) ? null : NormalizeRoleId(assignment.RequiredRole);
        var fallbackRole = string.IsNullOrWhiteSpace(assignment.FallbackRole) ? null : NormalizeRoleId(assignment.FallbackRole);

        return type == "role" && role is null && path is null && requiredRole is null && fallbackRole is null
            ? null
            : new WorkflowAssignmentDefinition(type, role, path, requiredRole, fallbackRole);
    }

    static EntityDisplayDefinition ResolveDisplay(ErpEntity entity, IReadOnlyList<FieldDefinition> fields)
    {
        var primary = string.IsNullOrWhiteSpace(entity.Display.Primary)
            ? InferDisplayField(fields)
            : entity.Display.Primary.Trim();
        var secondary = string.IsNullOrWhiteSpace(entity.Display.Secondary)
            ? InferSecondaryDisplayField(fields, primary)
            : entity.Display.Secondary.Trim();

        return new EntityDisplayDefinition(primary, string.IsNullOrWhiteSpace(secondary) ? null : secondary);
    }

    static string InferDisplayField(IReadOnlyList<FieldDefinition> fields)
    {
        var candidates = new[] { "name", "title", "email", "company_name", "full_name", "invoice_number" };
        foreach (var candidate in candidates)
        {
            var field = fields.FirstOrDefault(field => field.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return field.Name;
            }
        }

        return fields.FirstOrDefault(IsTextLike)?.Name ?? "id";
    }

    static string? InferSecondaryDisplayField(IReadOnlyList<FieldDefinition> fields, string primary)
    {
        var candidates = new[] { "email", "contact_email", "company_name", "customer_id", "role", "status" };
        foreach (var candidate in candidates)
        {
            var field = fields.FirstOrDefault(field =>
                !field.Name.Equals(primary, StringComparison.OrdinalIgnoreCase) &&
                field.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return field.Name;
            }
        }

        return fields.FirstOrDefault(field => !field.Name.Equals(primary, StringComparison.OrdinalIgnoreCase) && IsTextLike(field))?.Name;
    }

    static bool IsTextLike(FieldDefinition field) =>
        field.Type.Equals("string", StringComparison.OrdinalIgnoreCase) ||
        field.Type.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        field.Type.Equals("enum", StringComparison.OrdinalIgnoreCase);

    static string ToSqlType(ErpField field) => field.Type.ToLowerInvariant() switch
    {
        "string" => "varchar(500)",
        "text" => "text",
        "integer" => "integer",
        "number" => "numeric",
        "decimal" => "numeric",
        "money" => "numeric(18,2)",
        "boolean" => "boolean",
        "date" => "date",
        "datetime" => "timestamptz",
        "relation" => "bigint",
        "enum" => "varchar(160)",
        _ => throw new InvalidOperationException($"Unsupported field type '{field.Type}'.")
    };

    static EntityDefinition? ResolveEntity(string entityKey, IReadOnlyList<EntityDefinition> entities)
    {
        if (string.IsNullOrWhiteSpace(entityKey))
        {
            return null;
        }

        var normalized = entityKey.Trim();
        return entities.FirstOrDefault(entity =>
                   entity.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                   entity.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                   entity.TableName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
               ?? entities.FirstOrDefault(entity =>
                   normalized.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
                   entity.Id.Equals(normalized[..^1], StringComparison.OrdinalIgnoreCase));
    }
}
