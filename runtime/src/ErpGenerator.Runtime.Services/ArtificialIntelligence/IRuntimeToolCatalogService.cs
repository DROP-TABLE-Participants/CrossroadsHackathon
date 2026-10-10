using ErpGenerator.Runtime.Shared.ArtificialIntelligence;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.ArtificialIntelligence;

public interface IRuntimeToolCatalogService
{
    Task<IReadOnlyList<RuntimeToolDefinition>> ListAuthorizedAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default);
}
