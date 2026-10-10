using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeFeedbackInputModel(
    string Host,
    string TenantStableId,
    string TenantSlug,
    string Environment,
    string RuntimeUserId,
    string RuntimeUserEmail,
    string? Category,
    string Message,
    string? EntityKey,
    string? RecordId,
    string? PagePath,
    IReadOnlyDictionary<string, object?>? Metadata);
