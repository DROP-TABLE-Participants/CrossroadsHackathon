using ErpGenerator.Runtime.Services.Runtime;
using ErpGenerator.Runtime.Shared.ArtificialIntelligence;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Assistant;

public interface IErpAssistantService
{
    Task<ErpAssistantAnswer> AskAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        IReadOnlyList<RuntimeEntityDefinition> visibleEntities,
        string question,
        RuntimeAssistantContext context,
        CancellationToken cancellationToken);
}
