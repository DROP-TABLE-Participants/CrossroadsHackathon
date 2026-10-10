namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class GenerationRunEventTypes
{
    public const string Queued = "queued";
    public const string LeaseClaimed = "lease_claimed";
    public const string LeaseRecovered = "lease_recovered";
    public const string StageStarted = "stage_started";
    public const string StageCompleted = "stage_completed";
    public const string StageReused = "stage_reused";
    public const string WaitingForInput = "waiting_for_input";
    public const string InputReceived = "input_received";
    public const string RetryScheduled = "retry_scheduled";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string AgentWorkItemStarted = "agent_work_item_started";
    public const string AgentWorkItemCompleted = "agent_work_item_completed";
    public const string AgentWorkItemFailed = "agent_work_item_failed";
    public const string AgentWorkItemLeaseRecovered = "agent_work_item_lease_recovered";
    public const string SupervisorDecisionRecorded = "supervisor_decision_recorded";
    public const string ArtifactVersionCreated = "artifact_version_created";
}
