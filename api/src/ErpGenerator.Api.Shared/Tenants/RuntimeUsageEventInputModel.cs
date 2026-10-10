using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeUsageEventInputModel(
    string Host,
    string TenantStableId,
    string TenantSlug,
    string Environment,
    string RuntimeUserId,
    string RuntimeUserEmail,
    string RuntimeUserRole,
    string EventKind,
    DateTimeOffset OccurredAt,
    string? EntityKey = null,
    string? RecordId = null,
    string? WorkflowKey = null,
    string? ActionKey = null,
    string? ReportKey = null,
    string? PagePath = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);
