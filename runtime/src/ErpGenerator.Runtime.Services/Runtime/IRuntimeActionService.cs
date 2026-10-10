using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface IRuntimeActionService
{
    Task<ActionDefinition?> GetActionAsync(ResolvedTenantEnvironment tenant, string entityKey, string actionKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeRecordAction>> ListRecordActionsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, long recordId, CancellationToken cancellationToken = default);
    Task<RuntimeActionExecutionResult> ExecuteActionAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, long recordId, string actionKey, CancellationToken cancellationToken = default);
}
