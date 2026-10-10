namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class GenerationStageStatuses
{
    public const string Running = "running";
    public const string WaitingForInput = "waiting_for_input";
    public const string Cancelled = "cancelled";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
