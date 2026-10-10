using Npgsql;
using ErpGenerator.Runtime.Data.Databases.Scheduling;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    private static async Task AcquireDeploymentTransactionFenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string lockKey,
        CancellationToken cancellationToken)
    {
        // The durable lease is the admission/observability boundary. This
        // transaction-scoped advisory lock is the final correctness boundary:
        // even if a long backup outlives its lease, authoritative bundle state
        // can only be recomputed and mutated by one transaction at a time.
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@lockKey, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lockKey", lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Bundle deployment/rollback and the tenant scheduler must have one
        // outer lock order before either side touches generated app tables or
        // runtime definitions. This is the exact database-scoped fence also
        // held by acceptance execution and RuntimeScheduleCycle.
        await using var runtimeMutationFence = new NpgsqlCommand(
            $"select pg_advisory_xact_lock({RuntimeScheduleStore.AcceptanceAdvisoryLockNamespace}, hashtext(current_database()))",
            connection,
            transaction);
        await runtimeMutationFence.ExecuteNonQueryAsync(cancellationToken);
    }
}
