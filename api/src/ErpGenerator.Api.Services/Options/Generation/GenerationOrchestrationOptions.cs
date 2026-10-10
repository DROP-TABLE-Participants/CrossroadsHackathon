namespace ErpGenerator.Api.Services.Options.Generation;

public sealed class GenerationOrchestrationOptions
{
    public const string SectionName = "GenerationOrchestration";

    public int LeaseDurationSeconds { get; set; } = 1_800;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int MaximumAttempts { get; set; } = 3;
    public int InitialRetryDelaySeconds { get; set; } = 15;
    public int MaximumRetryDelaySeconds { get; set; } = 300;
    public int WorkerIdleDelayMilliseconds { get; set; } = 2_000;
    public int WorkerFailureDelayMilliseconds { get; set; } = 5_000;
    public int MaximumAgentAttempts { get; set; } = 3;
    public int MaximumAgentRepairs { get; set; } = 2;
    public int MaximumRoutingDepth { get; set; } = 2;
}
