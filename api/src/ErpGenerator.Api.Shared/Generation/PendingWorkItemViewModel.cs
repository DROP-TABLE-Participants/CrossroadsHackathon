namespace ErpGenerator.Api.Shared.Generation;

public sealed record PendingWorkItemViewModel(
    Guid Id,
    string Kind,
    string Status,
    bool Blocking,
    string? TargetRole,
    string? Question,
    string? Reason,
    string? SourceStage,
    Guid? LinkedInterviewRequestId,
    string? ResolutionNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);
