namespace ErpGenerator.Api.Shared.Generation;

public sealed record GenerationProjectViewModel(
    Guid Id,
    Guid? TenantId,
    string CompanyName,
    string Goal,
    string DiscoveryMode,
    string Status,
    string? Industry,
    string? CompanyType,
    string? OwnerName,
    string? OwnerEmail,
    IReadOnlyList<GenerationProjectParticipantViewModel> Participants,
    IReadOnlyList<InterviewRequestViewModel> Requests,
    IReadOnlyList<GenerationRunViewModel> GenerationRuns,
    int InputSourceCount,
    IReadOnlyList<GenerationInputSourceViewModel> InputSources,
    IReadOnlyList<string> AvailableConnectors,
    DateTimeOffset CreatedAt,
    bool CanStartGeneration,
    string? GenerationBlockedReason);
