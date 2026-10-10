using System.Text.Json;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface IRuntimeRecordService
{
    Task<IReadOnlyList<RuntimeEntityDefinition>> ListEntitiesAsync(ResolvedTenantEnvironment tenant, CancellationToken cancellationToken = default);
    Task<RuntimeEntityDefinition?> GetEntityAsync(ResolvedTenantEnvironment tenant, string entityKey, CancellationToken cancellationToken = default);
    Task<RuntimeRecordList> ListRecordsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, RuntimeRecordQuery query, CancellationToken cancellationToken = default);
    Task<Dictionary<string, object?>?> GetRecordAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, long recordId, bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<Dictionary<string, object?>> CreateRecordAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken = default);
    Task<Dictionary<string, object?>?> UpdateRecordAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, long recordId, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken = default);
    Task<bool> ArchiveRecordAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, long recordId, string? archiveReason = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeLookupItem>> LookupRecordsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, string? search, int limit = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<RuntimeLookupItem>>> ListRelationOptionsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, RuntimeEntityDefinition entity, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, long>> CountRecordsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, IReadOnlyList<RuntimeEntityDefinition> entities, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, long>> CountRecordsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, IReadOnlyList<RuntimeEntityDefinition> entities, bool includeArchived, CancellationToken cancellationToken = default);
    Task<bool> RestoreRecordAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, long recordId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeAuditEvent>> ListAuditEventsAsync(ResolvedTenantEnvironment tenant, int limit = 50, CancellationToken cancellationToken = default);
    Task RecordAuditEventAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext? user, string eventType, string message, IReadOnlyDictionary<string, object?> metadata, CancellationToken cancellationToken = default);
}
