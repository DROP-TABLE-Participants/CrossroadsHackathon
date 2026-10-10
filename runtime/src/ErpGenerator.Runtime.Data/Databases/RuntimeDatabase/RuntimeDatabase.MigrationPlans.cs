using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    private static async Task<long> InsertMigrationPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long? fromBundleVersionId,
        long? toBundleVersionId,
        BundleMigrationPlan plan,
        string status,
        bool applied,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.bundle_migration_plans
              (from_bundle_version_id, to_bundle_version_id, status, risk_level, operations, blocked_reasons, applied_at)
            values
              (@fromBundleVersionId, @toBundleVersionId, @status, @riskLevel, cast(@operations as jsonb), cast(@blockedReasons as jsonb), case when @applied then now() else null end)
            returning id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("fromBundleVersionId", fromBundleVersionId is null ? DBNull.Value : fromBundleVersionId.Value);
        command.Parameters.AddWithValue("toBundleVersionId", toBundleVersionId is null ? DBNull.Value : toBundleVersionId.Value);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("riskLevel", plan.RiskLevel);
        command.Parameters.AddWithValue("operations", JsonSerializer.Serialize(plan.Operations, JsonOptions));
        command.Parameters.AddWithValue("blockedReasons", JsonSerializer.Serialize(plan.BlockedReasons, JsonOptions));
        command.Parameters.AddWithValue("applied", applied);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }
    private static async Task<bool> IsRollbackableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long currentBundleVersionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select operations
            from _runtime.bundle_migration_plans
            where to_bundle_version_id = @currentBundleVersionId and status = 'applied'
            order by applied_at desc nulls last, created_at desc
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("currentBundleVersionId", currentBundleVersionId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        var operations = JsonSerializer.Deserialize<IReadOnlyList<BundleMigrationOperation>>(json, JsonOptions) ?? [];
        return operations.All(operation => operation.Rollbackable);
    }
}
