namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class GenerationRunPresentationStatus
{
    public const string Resuming = "resuming";
    public const string ResumeFailed = "resume_failed";
    public const int ResumeFailureAttentionThreshold = 3;

    public static string For(GenerationRun run) =>
        run.Status == GenerationRunStatuses.WaitingForInput &&
        !run.PendingWorkItems.Any(value =>
            value.Blocking &&
            value.Status == "pending")
            ? run.ResumeFailureCount >= ResumeFailureAttentionThreshold
                ? ResumeFailed
                : Resuming
            : run.Status;
}
