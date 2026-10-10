namespace ErpGenerator.Api.Shared.Generation;

public sealed record PublicInterviewPendingWorkViewModel(
    Guid Id,
    string Question,
    string? TargetRole,
    string SourceStage,
    string Status);
