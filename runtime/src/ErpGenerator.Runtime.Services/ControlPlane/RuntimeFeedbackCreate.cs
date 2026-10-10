namespace ErpGenerator.Runtime.Services.ControlPlane;

public sealed record RuntimeFeedbackCreate(
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
