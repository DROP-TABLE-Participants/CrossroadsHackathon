using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Shared.ArtificialIntelligence;

public static class RuntimeToolCatalogBuilder
{
    public static IReadOnlyList<RuntimeToolDefinition> Build(CompiledBundle bundle)
    {
        var tools = new List<RuntimeToolDefinition>();
        foreach (var entity in bundle.Entities)
        {
            tools.Add(ReadEntityTool(
                "records.list",
                "List records",
                entity,
                [
                    new RuntimeToolFieldDefinition("page", "integer", false, "One-based page number."),
                    new RuntimeToolFieldDefinition("pageSize", "integer", false, "Maximum records to return.")
                ]));
            tools.Add(ReadEntityTool(
                "records.get",
                "Get record",
                entity,
                [new RuntimeToolFieldDefinition("recordId", "integer", true, "Canonical record identifier.")]));
            tools.Add(PrepareEntityTool(
                "records.prepare_create",
                "Prepare record creation",
                entity,
                ActionPermissionEffectAnalyzer.EffectCreate));
            tools.Add(PrepareEntityTool(
                "records.prepare_update",
                "Prepare record update",
                entity,
                ActionPermissionEffectAnalyzer.EffectUpdate));
        }

        tools.AddRange(bundle.Reports.Select(report =>
            Definition(
                $"reports.execute:{report.Key}",
                "Execute calculated report",
                report.Description ?? report.Label,
                "reports",
                RuntimeToolModes.Read,
                RuntimeToolRisks.ReadOnly,
                "report",
                report.Key,
                [],
                [new RuntimeToolFieldDefinition("result", "report_result", true, "Calculated report result.")],
                [new RuntimeToolPermissionRequirement(PermissionGrantScopes.Page, GranularPermissionActions.View, report.Key, "report_detail")],
                [])));

        tools.Add(Definition(
            "tasks.list",
            "List tasks",
            "Lists workflow tasks visible to the current user.",
            "workflows",
            RuntimeToolModes.Read,
            RuntimeToolRisks.ReadOnly,
            "task",
            "*",
            [],
            [new RuntimeToolFieldDefinition("tasks", "task[]", true, "Permission-filtered tasks.")],
            [new RuntimeToolPermissionRequirement(PermissionGrantScopes.Entity, "read_task", "*")],
            []));

        tools.AddRange(bundle.Workflows.Select(workflow =>
            Definition(
                $"workflows.get:{workflow.Id}",
                "Get workflow",
                workflow.Name,
                "workflows",
                RuntimeToolModes.Read,
                RuntimeToolRisks.ReadOnly,
                "workflow",
                workflow.Id,
                [new RuntimeToolFieldDefinition("instanceId", "integer", true, "Canonical workflow instance identifier.")],
                [new RuntimeToolFieldDefinition("workflow", "workflow_instance", true, "Permission-filtered workflow instance.")],
                [new RuntimeToolPermissionRequirement(PermissionGrantScopes.Entity, "read", workflow.Entity)],
                [])));

        tools.Add(Definition(
            "audit.get",
            "Get audit events",
            "Lists tenant-local audit events for authorized administrators.",
            "audit",
            RuntimeToolModes.Read,
            RuntimeToolRisks.ReadOnly,
            "audit",
            "*",
            [],
            [new RuntimeToolFieldDefinition("events", "audit_event[]", true, "Tenant-local audit events.")],
            [new RuntimeToolPermissionRequirement(PermissionGrantScopes.Entity, "admin", "*")],
            []));

        tools.AddRange(bundle.Actions.Select(action =>
            Definition(
                $"actions.prepare_execute:{action.Key}",
                "Prepare action",
                action.Description ?? action.Label,
                "actions",
                RuntimeToolModes.Prepare,
                RuntimeToolRisks.Consequential,
                "action",
                action.Key,
                [new RuntimeToolFieldDefinition("recordId", "integer", true, "Canonical record identifier.")],
                [new RuntimeToolFieldDefinition("preview", "action_preview", true, "Non-executable action preview.")],
                [new RuntimeToolPermissionRequirement(PermissionGrantScopes.Action, "execute", action.Entity, action.Key)],
                ActionPermissionEffectAnalyzer.Analyze(
                        action.Entity,
                        action.Steps.Select(step => new ActionPermissionEffectStep(
                            step.Id,
                            step.Type,
                            step.Entity,
                            step.ForEach?.Result,
                            step.RecordsFrom?.Result)).ToArray())
                    .Requirements
                    .Select(effect => new RuntimeToolEffect(effect.Action, effect.Entity))
                    .ToArray(),
                enabled: false)));

        tools.AddRange(bundle.Workflows.Select(workflow =>
            Definition(
                $"workflows.prepare_start:{workflow.Id}",
                "Prepare workflow start",
                workflow.Name,
                "workflows",
                RuntimeToolModes.Prepare,
                RuntimeToolRisks.Consequential,
                "workflow",
                workflow.Id,
                [new RuntimeToolFieldDefinition("recordId", "integer", true, "Canonical record identifier.")],
                [new RuntimeToolFieldDefinition("preview", "action_preview", true, "Non-executable workflow preview.")],
                [new RuntimeToolPermissionRequirement(PermissionGrantScopes.Entity, "start_workflow", workflow.Entity)],
                [new RuntimeToolEffect("start_workflow", workflow.Entity, workflow.Id)],
                enabled: false)));

        tools.Add(Definition(
            "tasks.prepare_decision",
            "Prepare task decision",
            "Prepares an approval or rejection without executing it.",
            "workflows",
            RuntimeToolModes.Prepare,
            RuntimeToolRisks.Consequential,
            "task",
            "*",
            [
                new RuntimeToolFieldDefinition("taskId", "integer", true, "Canonical task identifier."),
                new RuntimeToolFieldDefinition("decision", "string", true, "approve or reject.")
            ],
            [new RuntimeToolFieldDefinition("preview", "action_preview", true, "Non-executable task decision preview.")],
            [new RuntimeToolPermissionRequirement(PermissionGrantScopes.Entity, "approve_task", "*")],
            [new RuntimeToolEffect("decide_task", "*")],
            enabled: false));

        return tools
            .OrderBy(tool => tool.Key, StringComparer.Ordinal)
            .Select(tool => tool with { BundleHash = bundle.BundleHash })
            .ToArray();
    }

    private static RuntimeToolDefinition ReadEntityTool(
        string key,
        string label,
        EntityDefinition entity,
        IReadOnlyList<RuntimeToolFieldDefinition> input) =>
        Definition(
            $"{key}:{entity.Id}",
            label,
            $"{label} for {entity.DisplayName}.",
            "records",
            RuntimeToolModes.Read,
            RuntimeToolRisks.ReadOnly,
            "entity",
            entity.Id,
            input,
            [new RuntimeToolFieldDefinition("records", "record[]", true, "Permission-filtered records.")],
            [new RuntimeToolPermissionRequirement(PermissionGrantScopes.Entity, "read", entity.Id)],
            []);

    private static RuntimeToolDefinition PrepareEntityTool(
        string key,
        string label,
        EntityDefinition entity,
        string effect) =>
        Definition(
            $"{key}:{entity.Id}",
            label,
            $"{label} for {entity.DisplayName}.",
            "records",
            RuntimeToolModes.Prepare,
            RuntimeToolRisks.Consequential,
            "entity",
            entity.Id,
            [],
            [new RuntimeToolFieldDefinition("preview", "action_preview", true, "Non-executable change preview.")],
            [new RuntimeToolPermissionRequirement(PermissionGrantScopes.Entity, effect, entity.Id)],
            [new RuntimeToolEffect(effect, entity.Id)],
            enabled: false);

    private static RuntimeToolDefinition Definition(
        string key,
        string label,
        string description,
        string category,
        string mode,
        string risk,
        string resourceType,
        string resourceKey,
        IReadOnlyList<RuntimeToolFieldDefinition> input,
        IReadOnlyList<RuntimeToolFieldDefinition> output,
        IReadOnlyList<RuntimeToolPermissionRequirement> permissions,
        IReadOnlyList<RuntimeToolEffect> effects,
        bool enabled = true) =>
        new(
            key,
            label,
            description,
            category,
            mode,
            risk,
            resourceType,
            resourceKey,
            input,
            output,
            permissions,
            effects)
        {
            Enabled = enabled
        };
}
