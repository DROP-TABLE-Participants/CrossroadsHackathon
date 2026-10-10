namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class AgentWorkItemStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string WaitingForInput = "waiting_for_input";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
