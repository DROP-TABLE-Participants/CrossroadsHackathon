using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;

namespace ErpGenerator.Runtime.WebHost.Scheduling;

public sealed class RuntimeSchedulerHostedService : BackgroundService
{
    private readonly IControlPlaneClient controlPlane;
    private readonly IRuntimeScheduleCycle cycle;
    private readonly RuntimeSchedulerHostedOptions options;
    private readonly ILogger<RuntimeSchedulerHostedService> logger;
    private readonly string workerId;

    public RuntimeSchedulerHostedService(
        IControlPlaneClient controlPlane,
        IRuntimeScheduleCycle cycle,
        IOptions<RuntimeSchedulerHostedOptions> options,
        ILogger<RuntimeSchedulerHostedService> logger)
    {
        this.controlPlane = controlPlane;
        this.cycle = cycle;
        this.options = options.Value;
        this.options.Validate();
        this.logger = logger;
        workerId = BuildWorkerId(this.options.WorkerId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Tenant runtime scheduler is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Tenant runtime scheduler pass failed before all target environments were enumerated.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.PollIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<RuntimeSchedulerPassResult> RunPassAsync(
        CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var enumerated = 0;
        var completed = 0;
        var failed = 0;
        for (var page = 1; page <= options.MaxPagesPerPass; page++)
        {
            var targets = await controlPlane.ListScheduleTargetsAsync(
                page,
                options.PageSize,
                cancellationToken);
            if (targets.Page != page)
            {
                throw new InvalidOperationException(
                    "Control-plane scheduler pagination changed during an exact pass.");
            }

            var pageTargets = new List<ResolvedTenantEnvironment>(targets.Targets.Count);
            foreach (var target in targets.Targets)
            {
                var key = $"{target.TenantStableId}:{target.Environment}";
                if (!seen.Add(key))
                {
                    throw new InvalidOperationException(
                        "Control-plane scheduler enumeration returned a duplicate tenant environment.");
                }

                pageTargets.Add(target);
            }

            enumerated += pageTargets.Count;
            var pageCompleted = 0;
            var pageFailed = 0;
            await Parallel.ForEachAsync(
                pageTargets,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.MaxTenantConcurrency,
                    CancellationToken = cancellationToken
                },
                async (target, token) =>
                {
                    try
                    {
                        var result = await cycle.RunOnceAsync(target, workerId, token);
                        Interlocked.Increment(ref pageCompleted);
                        if (result.Claimed > 0 || result.Reclaimed > 0 || result.Cancelled > 0)
                        {
                            logger.LogInformation(
                                "Tenant scheduler pass for {TenantStableId} {Environment}: claimed {Claimed}, completed {Completed}, retried {Retried}, exhausted {Exhausted}, reclaimed {Reclaimed}, cancelled {Cancelled}, leaseLost {LeaseLost}.",
                                target.TenantStableId,
                                target.Environment,
                                result.Claimed,
                                result.Completed,
                                result.Retried,
                                result.Exhausted,
                                result.Reclaimed,
                                result.Cancelled,
                                result.LeaseLost);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        Interlocked.Increment(ref pageFailed);
                        logger.LogError(
                            exception,
                            "Tenant scheduler cycle failed for {TenantStableId} {Environment}.",
                            target.TenantStableId,
                            target.Environment);
                    }
                });
            completed += pageCompleted;
            failed += pageFailed;

            if (!targets.HasNextPage)
            {
                return new RuntimeSchedulerPassResult(enumerated, completed, failed, page);
            }
        }

        throw new InvalidOperationException(
            "Control-plane scheduler enumeration exceeded its bounded page limit.");
    }

    private static string BuildWorkerId(string configured)
    {
        var value = string.IsNullOrWhiteSpace(configured)
            ? $"{Environment.MachineName}:{Environment.ProcessId}"
            : configured.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120)
        {
            throw new InvalidOperationException(
                "Runtime scheduler worker identity is empty or exceeds 120 characters.");
        }

        return value;
    }
}

public sealed record RuntimeSchedulerPassResult(
    int Enumerated,
    int Completed,
    int Failed,
    int Pages);
