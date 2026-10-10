using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface IRuntimePermissionService
{
    Task<bool> IsAllowedAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string action, RuntimeEntityDefinition? entity, CancellationToken cancellationToken = default);
    Task<bool> IsFieldAllowedAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string action, RuntimeEntityDefinition entity, string field, CancellationToken cancellationToken = default);
    Task<bool> AreFieldsAllowedAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string action, RuntimeEntityDefinition entity, IEnumerable<string> fields, CancellationToken cancellationToken = default);
    Task<bool> IsPageAllowedAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string page, string resource, CancellationToken cancellationToken = default);
    Task<bool> IsNamedActionAllowedAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, RuntimeEntityDefinition entity, string actionKey, CancellationToken cancellationToken = default);
    Task<RuntimeEntityDefinition> FilterReadableEntityAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, RuntimeEntityDefinition entity, CancellationToken cancellationToken = default);
    Task<RuntimeRecordList> FilterReadableRecordListFieldsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, RuntimeRecordList list, CancellationToken cancellationToken = default);
    Task<Dictionary<string, object?>> FilterReadableRecordFieldsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, RuntimeEntityDefinition entity, IReadOnlyDictionary<string, object?> record, CancellationToken cancellationToken = default);
}
