namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminPendingWorkViewModel(
    Guid Id,
    string Kind,
    string Status,
    bool Blocking,
    string Prompt,
    string? TargetRole,
    DateTimeOffset CreatedAt);
