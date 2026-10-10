namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class GenerationRoutingPolicy
{
    public static GenerationRoute ForStageOutcome(GenerationStageOutcome outcome) =>
        outcome switch
        {
            GenerationStageOutcome.Completed => GenerationRoute.Advance,
            GenerationStageOutcome.WaitingForInput => GenerationRoute.WaitForInput,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };

    public static GenerationRoute ForFailure(
        GenerationFailure failure,
        int failureCount,
        int maximumAttempts)
    {
        if (failureCount < 1)
            throw new ArgumentOutOfRangeException(
                nameof(failureCount),
                "Failure count must include the current failure.");
        if (maximumAttempts < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maximumAttempts),
                "Maximum attempts must be positive.");

        return failure.Disposition == GenerationFailureDispositions.Retryable &&
               failureCount < maximumAttempts
            ? GenerationRoute.Retry
            : GenerationRoute.Fail;
    }

    public static GenerationRoute ForRunStatus(string status) =>
        status == GenerationRunStatuses.CancellationRequested
            ? GenerationRoute.Cancel
            : GenerationRoute.Advance;
}
