namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminOrchestrationOverviewViewModel(
    int QueuedRunCount,
    int RunningRunCount,
    int ResumingRunCount,
    int ResumeFailedRunCount,
    int WaitingForInputRunCount,
    int CancellationRequestedRunCount,
    int RetryScheduledRunCount,
    int FailedLast24HoursCount,
    int StaleLeaseCount,
    DateTimeOffset? OldestQueuedAt,
    IReadOnlyList<AdminGenerationRunListItemViewModel> RecentRuns);
