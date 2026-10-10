using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Shared.Scheduling;

public static class RuntimeScheduleJobStatuses
{
    public const string Ready = "ready";
    public const string Running = "running";
    public const string Retry = "retry";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Superseded = "superseded";
    public const string Exhausted = "exhausted";
}

public static class RuntimeScheduleErrorCodes
{
    public const string LeaseExpired = "schedule.lease_expired";
    public const string WorkflowFailed = "schedule.workflow_failed";
    public const string LeaseLost = "schedule.lease_lost";
}

public sealed class RuntimeScheduleOptions
{
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromHours(1);

    public int MaxAttempts { get; init; } = 5;

    public int MaxJobsPerCycle { get; init; } = 25;

    public void Validate()
    {
        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        }

        if (HeartbeatInterval <= TimeSpan.Zero || HeartbeatInterval >= LeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval));
        }

        if (BaseRetryDelay <= TimeSpan.Zero || MaxRetryDelay < BaseRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseRetryDelay));
        }

        if (MaxAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        }

        if (MaxJobsPerCycle is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxJobsPerCycle));
        }
    }
}

public sealed record RuntimeScheduledJob(
    long Id,
    string ScheduleId,
    string ScheduleDefinitionHash,
    string EntityId,
    long RecordId,
    DateTimeOffset DueAt,
    string Status,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset AvailableAt,
    Guid? LeaseId,
    string? LeasedBy,
    DateTimeOffset? LeaseExpiresAt,
    long? WorkflowInstanceId,
    Guid? AcceptanceRunId,
    string? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt)
{
    public RuntimeScheduleLease Lease =>
        Status == RuntimeScheduleJobStatuses.Running &&
        LeaseId is not null &&
        !string.IsNullOrWhiteSpace(LeasedBy) &&
        LeaseExpiresAt is not null
            ? new RuntimeScheduleLease(
                Id,
                ScheduleDefinitionHash,
                LeaseId.Value,
                LeasedBy,
                LeaseExpiresAt.Value)
            : throw new InvalidOperationException("The scheduled job does not hold an active lease.");
}

public sealed record RuntimeScheduleLease(
    long JobId,
    string ScheduleDefinitionHash,
    Guid LeaseId,
    string WorkerId,
    DateTimeOffset ExpiresAt);

public sealed record RuntimeScheduleRefreshResult(
    int MaterializedOrUpdated,
    int Cancelled,
    int Reclaimed,
    int Exhausted);

public sealed record RuntimeScheduledWorkflowExecutionResult(long WorkflowInstanceId);

public sealed class RuntimeScheduleLeaseLostException(string message)
    : Exception(message)
{
    public const string StableCode = "schedule.lease_lost";
}

/// <summary>
/// The executor is the authoritative exactly-once boundary. Implementations
/// must lock and revalidate the leased scheduled job and its current definition,
/// start/apply the workflow, bind workflow_instances.scheduler_job_id, and mark
/// the job completed in one tenant-database transaction. Implementations must
/// never synthesize a business user or start through the public user action API.
/// </summary>
public interface IRuntimeScheduledWorkflowExecutor
{
    Task<RuntimeScheduledWorkflowExecutionResult> ExecuteAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeScheduledJob job,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeScheduleCycle
{
    Task<RuntimeScheduleCycleResult> RunOnceAsync(
        ResolvedTenantEnvironment tenant,
        string workerId,
        CancellationToken cancellationToken = default);
}

public sealed record RuntimeScheduleCycleResult(
    bool AcceptanceFenceAcquired,
    int MaterializedOrUpdated,
    int Cancelled,
    int Reclaimed,
    int Exhausted,
    int Claimed,
    int Completed,
    int Retried,
    int LeaseLost);
