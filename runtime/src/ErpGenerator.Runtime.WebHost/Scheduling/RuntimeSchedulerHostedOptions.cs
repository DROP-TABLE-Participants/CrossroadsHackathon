namespace ErpGenerator.Runtime.WebHost.Scheduling;

public sealed class RuntimeSchedulerHostedOptions
{
    public bool Enabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 15;

    public int PageSize { get; set; } = 100;

    public int MaxPagesPerPass { get; set; } = 1_000;

    public int MaxTenantConcurrency { get; set; } = 4;

    public string WorkerId { get; set; } = string.Empty;

    internal void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 3_600)
        {
            throw new InvalidOperationException(
                "RuntimeScheduler:PollIntervalSeconds must be between 1 and 3600.");
        }

        if (PageSize is < 1 or > 100)
        {
            throw new InvalidOperationException(
                "RuntimeScheduler:PageSize must be between 1 and 100.");
        }

        if (MaxPagesPerPass is < 1 or > 10_000)
        {
            throw new InvalidOperationException(
                "RuntimeScheduler:MaxPagesPerPass must be between 1 and 10000.");
        }

        if (MaxTenantConcurrency is < 1 or > 64)
        {
            throw new InvalidOperationException(
                "RuntimeScheduler:MaxTenantConcurrency must be between 1 and 64.");
        }

        if (WorkerId.Length > 120)
        {
            throw new InvalidOperationException(
                "RuntimeScheduler:WorkerId cannot exceed 120 characters.");
        }
    }
}
