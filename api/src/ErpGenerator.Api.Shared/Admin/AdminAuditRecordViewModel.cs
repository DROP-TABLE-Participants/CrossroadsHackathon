namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminAuditRecordViewModel(
    Guid Id,
    Guid ActorAdminAccountId,
    string ActorEmail,
    string Action,
    string ResourceType,
    string ResourceId,
    string SafeDetailsJson,
    DateTimeOffset CreatedAt);
