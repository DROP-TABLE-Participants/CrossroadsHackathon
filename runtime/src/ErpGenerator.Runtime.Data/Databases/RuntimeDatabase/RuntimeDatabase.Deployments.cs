using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    private static async Task<long> InsertBundleDeploymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long? bundleVersionId,
        long? migrationPlanId,
        string environment,
        string status,
        string? errorMessage,
        object metadata,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.bundle_deployments
              (bundle_version_id, migration_plan_id, environment, status, finished_at, error_message, metadata)
            values
              (@bundleVersionId, @migrationPlanId, @environment, @status, now(), @errorMessage, cast(@metadata as jsonb))
            returning id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("bundleVersionId", bundleVersionId is null ? DBNull.Value : bundleVersionId.Value);
        command.Parameters.AddWithValue("migrationPlanId", migrationPlanId is null ? DBNull.Value : migrationPlanId.Value);
        command.Parameters.AddWithValue("environment", environment);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("errorMessage", string.IsNullOrWhiteSpace(errorMessage) || status != "failed" ? DBNull.Value : errorMessage);
        command.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(metadata, JsonOptions));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }
}
