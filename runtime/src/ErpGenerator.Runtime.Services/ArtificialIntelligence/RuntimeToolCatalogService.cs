using ErpGenerator.Runtime.Shared.ArtificialIntelligence;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.ArtificialIntelligence;

public sealed class RuntimeToolCatalogService(
    RuntimeActiveBundleReader bundles,
    IRuntimeRecordService records,
    IRuntimePermissionService permissions) : IRuntimeToolCatalogService
{
    public async Task<IReadOnlyList<RuntimeToolDefinition>> ListAuthorizedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
    {
        var bundle = await bundles.ReadAsync(tenant, cancellationToken);
        var entities = await records.ListEntitiesAsync(tenant, cancellationToken);
        var entityLookup = entities.ToDictionary(entity => entity.Id, StringComparer.Ordinal);
        var result = new List<RuntimeToolDefinition>();
        foreach (var tool in RuntimeToolCatalogBuilder.Build(bundle))
        {
            if (!tool.Enabled && !includeDisabled)
            {
                continue;
            }

            if (await IsAuthorizedAsync(
                    tenant,
                    user,
                    tool,
                    bundle,
                    entityLookup,
                    cancellationToken))
            {
                result.Add(tool);
            }
        }

        return result;
    }

    private async Task<bool> IsAuthorizedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        RuntimeToolDefinition tool,
        CompiledBundle bundle,
        IReadOnlyDictionary<string, RuntimeEntityDefinition> entities,
        CancellationToken cancellationToken)
    {
        if (tool.Key.StartsWith("records.", StringComparison.Ordinal))
        {
            return entities.TryGetValue(tool.ResourceKey, out var entity) &&
                   await permissions.IsAllowedAsync(
                       tenant,
                       user,
                       RequiredEntityAction(tool),
                       entity,
                       cancellationToken);
        }

        if (tool.Key.StartsWith("reports.execute:", StringComparison.Ordinal))
        {
            var report = bundle.Reports.Single(candidate => candidate.Key == tool.ResourceKey);
            return entities.TryGetValue(report.SourceEntity, out var entity) &&
                   await permissions.IsAllowedAsync(tenant, user, "read", entity, cancellationToken) &&
                   await permissions.IsPageAllowedAsync(
                       tenant,
                       user,
                       "report_detail",
                       report.Key,
                       cancellationToken);
        }

        if (tool.Key.Equals("tasks.list", StringComparison.Ordinal) ||
            tool.Key.Equals("tasks.prepare_decision", StringComparison.Ordinal))
        {
            var action = tool.Mode == RuntimeToolModes.Read ? "read_task" : "approve_task";
            return await AnyEntityAllowedAsync(tenant, user, entities.Values, action, cancellationToken);
        }

        if (tool.Key.StartsWith("workflows.", StringComparison.Ordinal))
        {
            var workflow = bundle.Workflows.Single(candidate => candidate.Id == tool.ResourceKey);
            return entities.TryGetValue(workflow.Entity, out var entity) &&
                   await permissions.IsAllowedAsync(
                       tenant,
                       user,
                       tool.Mode == RuntimeToolModes.Read ? "read" : "start_workflow",
                       entity,
                       cancellationToken);
        }

        if (tool.Key.StartsWith("actions.prepare_execute:", StringComparison.Ordinal))
        {
            var action = bundle.Actions.Single(candidate => candidate.Key == tool.ResourceKey);
            if (!entities.TryGetValue(action.Entity, out var entity) ||
                !await permissions.IsNamedActionAllowedAsync(
                    tenant,
                    user,
                    entity,
                    action.Key,
                    cancellationToken))
            {
                return false;
            }

            foreach (var effect in tool.Effects)
            {
                if (!entities.TryGetValue(effect.Entity, out var affectedEntity) ||
                    !await permissions.IsAllowedAsync(
                        tenant,
                        user,
                        ActionPermissionEffectAnalyzer.EffectActionFor(effect.Action) ?? effect.Action,
                        affectedEntity,
                        cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        return tool.Key.Equals("audit.get", StringComparison.Ordinal) &&
               await permissions.IsAllowedAsync(tenant, user, "admin", null, cancellationToken);
    }

    private static string RequiredEntityAction(RuntimeToolDefinition tool)
    {
        var requirement = tool.Permissions.Single(permission =>
            permission.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.Ordinal));
        return requirement.Action;
    }

    private async Task<bool> AnyEntityAllowedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        IEnumerable<RuntimeEntityDefinition> entities,
        string action,
        CancellationToken cancellationToken)
    {
        foreach (var entity in entities)
        {
            if (await permissions.IsAllowedAsync(tenant, user, action, entity, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
