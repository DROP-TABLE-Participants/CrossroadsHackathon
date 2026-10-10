using System.Data;
using Npgsql;
using ErpGenerator.Runtime.Data.Databases.Scheduling;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

/// <summary>
/// Runs one bounded tenant-local scheduler pass while holding the exact same
/// database advisory fence used by acceptance execution. The injected workflow
/// executor owns the transactional start-and-complete boundary described by
/// <see cref="IRuntimeScheduledWorkflowExecutor"/>.
/// </summary>
public sealed class RuntimeScheduleCycle : IRuntimeScheduleCycle
{
    private readonly RuntimeScheduleStore store;
    private readonly IRuntimeScheduledWorkflowExecutor executor;
    private readonly RuntimeScheduleOptions options;

    public RuntimeScheduleCycle(
        RuntimeScheduleStore store,
        IRuntimeScheduledWorkflowExecutor executor,
        RuntimeScheduleOptions? options = null)
    {
        this.store = store;
        this.executor = executor;
        this.options = options ?? new RuntimeScheduleOptions();
        this.options.Validate();
    }

    public async Task<RuntimeScheduleCycleResult> RunOnceAsync(
        ResolvedTenantEnvironment tenant,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(tenant, cancellationToken);
        if (!await RuntimeScheduleStore.HasSchedulerSchemaAsync(connection, cancellationToken))
        {
            // Tenant environments created before scheduler metadata existed are
            // valid runtime targets. They simply have no scheduled work until a
            // current bundle is deployed, so polling them must be a quiet no-op.
            return new RuntimeScheduleCycleResult(false, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        if (!await RuntimeScheduleStore.TryAcquireAcceptanceFenceAsync(connection, cancellationToken))
        {
            return new RuntimeScheduleCycleResult(false, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        try
        {
            return await RunCoreAsync(tenant, workerId, connection, cancellationToken);
        }
        finally
        {
            await RuntimeScheduleStore.ReleaseAcceptanceFenceAsync(connection);
        }
    }

    /// <summary>
    /// Runs one scheduler pass on the exact session that already owns the
    /// scheduler/acceptance advisory fence. This is the acceptance-runner seam:
    /// it neither re-enters nor releases the caller's session-level lock.
    /// </summary>
    internal async Task<RuntimeScheduleCycleResult> RunOnceWithOwnedAcceptanceFenceAsync(
        ResolvedTenantEnvironment tenant,
        string workerId,
        NpgsqlConnection fenceOwnerConnection,
        CancellationToken cancellationToken = default)
    {
        await EnsureAcceptanceFenceOwnedAsync(tenant, fenceOwnerConnection, cancellationToken);
        return await RunCoreAsync(tenant, workerId, fenceOwnerConnection, cancellationToken);
    }

    internal async Task<RuntimeScheduleRefreshResult> RefreshWithOwnedAcceptanceFenceAsync(
        ResolvedTenantEnvironment tenant,
        NpgsqlConnection fenceOwnerConnection,
        CancellationToken cancellationToken = default)
    {
        await EnsureAcceptanceFenceOwnedAsync(tenant, fenceOwnerConnection, cancellationToken);
        return await store.RefreshAsync(fenceOwnerConnection, cancellationToken);
    }

    internal Task<IReadOnlyList<RuntimeScheduledJob>> ListJobsAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default) =>
        store.ListJobsAsync(tenant, cancellationToken);

    private async Task<RuntimeScheduleCycleResult> RunCoreAsync(
        ResolvedTenantEnvironment tenant,
        string workerId,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var refresh = await store.RefreshAsync(connection, cancellationToken);
        var claimed = 0;
        var completed = 0;
        var retried = 0;
        var exhausted = refresh.Exhausted;
        var leaseLost = 0;
        for (var index = 0; index < options.MaxJobsPerCycle; index++)
        {
            var job = await store.ClaimNextAsync(connection, workerId, cancellationToken);
            if (job is null)
            {
                break;
            }

            claimed++;
            try
            {
                // ExecuteAsync must re-lock and validate the exact lease and
                // definition before applying any workflow effect. The job row
                // lock is held through workflow application and job completion.
                var execution = await executor.ExecuteAsync(tenant, job, cancellationToken);
                if (await store.CompleteAsync(
                        connection,
                        job.Lease,
                        execution.WorkflowInstanceId,
                        cancellationToken))
                {
                    completed++;
                }
                else
                {
                    leaseLost++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RuntimeScheduleLeaseLostException)
            {
                // The executor rejected stale authority before applying any
                // workflow effect. Deployment/reclaim owns the current row.
                leaseLost++;
            }
            catch
            {
                var status = await store.FailAsync(
                    connection,
                    job,
                    RuntimeScheduleErrorCodes.WorkflowFailed,
                    cancellationToken);
                switch (status)
                {
                    case RuntimeScheduleJobStatuses.Retry:
                        retried++;
                        break;
                    case RuntimeScheduleJobStatuses.Exhausted:
                        exhausted++;
                        break;
                    default:
                        leaseLost++;
                        break;
                }
            }
        }

        return new RuntimeScheduleCycleResult(
            true,
            refresh.MaterializedOrUpdated,
            refresh.Cancelled,
            refresh.Reclaimed,
            exhausted,
            claimed,
            completed,
            retried,
            leaseLost);
    }

    private static async Task EnsureAcceptanceFenceOwnedAsync(
        ResolvedTenantEnvironment tenant,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The acceptance fence owner connection must be open.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select current_database() = @databaseName
               and exists (
                 select 1
                 from pg_locks
                 where locktype = 'advisory'
                   and pid = pg_backend_pid()
                   and granted
                   and mode = 'ExclusiveLock'
                   and classid::bigint = {RuntimeScheduleStore.AcceptanceAdvisoryLockNamespace}
                   and objid::bigint =
                       ((hashtext(current_database())::bigint + 4294967296) % 4294967296)
                   and objsubid = 2)
            """;
        command.Parameters.AddWithValue("databaseName", tenant.DatabaseName);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                "The supplied connection does not own the exact scheduler/acceptance database fence.");
        }
    }
}
