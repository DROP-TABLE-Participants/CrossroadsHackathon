namespace ErpGenerator.Api.Shared.Tenants;

public sealed record ErpFeedbackViewModel(
    Guid Id,
    Guid TenantId,
    Guid TenantEnvironmentId,
    string SourceEnvironment,
    string RuntimeUserEmail,
    string? EntityKey,
    string? RecordId,
    string? PagePath,
    string? Category,
    string Message,
    string Status,
    DateTimeOffset CreatedAt);
