namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class GenerationRunTransitionPolicy
{
    public static void EnsureAllowed(string current, string next)
    {
        if (current == next || IsAllowed(current, next)) return;
        throw new InvalidOperationException(
            $"Generation run cannot transition from '{current}' to '{next}'.");
    }

    private static bool IsAllowed(string current, string next) => (current, next) switch
    {
        (GenerationRunStatuses.Queued, GenerationRunStatuses.Running) => true,
        (GenerationRunStatuses.Running, GenerationRunStatuses.Queued) => true,
        (GenerationRunStatuses.Running, GenerationRunStatuses.WaitingForInput) => true,
        (GenerationRunStatuses.Running, GenerationRunStatuses.CancellationRequested) => true,
        (GenerationRunStatuses.Running, GenerationRunStatuses.Completed) => true,
        (GenerationRunStatuses.Running, GenerationRunStatuses.Failed) => true,
        (GenerationRunStatuses.Queued, GenerationRunStatuses.Cancelled) => true,
        (GenerationRunStatuses.WaitingForInput, GenerationRunStatuses.Cancelled) => true,
        (GenerationRunStatuses.CancellationRequested, GenerationRunStatuses.Cancelled) => true,
        (GenerationRunStatuses.Cancelled, GenerationRunStatuses.Queued) => true,
        (GenerationRunStatuses.WaitingForInput, GenerationRunStatuses.Queued) => true,
        (GenerationRunStatuses.Failed, GenerationRunStatuses.Queued) => true,
        _ => false
    };
}
