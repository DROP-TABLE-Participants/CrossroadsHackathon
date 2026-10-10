using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    public async Task<CompiledBundle?> ReadActiveCompiledBundleAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                select bv.metadata
                from _runtime.active_bundle ab
                join _runtime.bundle_versions bv on bv.id = ab.bundle_version_id
                where ab.id = true
                limit 1
                """,
                connection);
            var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CompiledBundle>(json, JsonOptions);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidCatalogName)
        {
            return null;
        }
    }

    public async Task<CompiledBundle?> ReadCompiledBundleByVersionAsync(
        ResolvedTenantEnvironment tenant,
        string version,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                select metadata
                from _runtime.bundle_versions
                where version = @version
                order by deployed_at desc
                limit 1
                """,
                connection);
            command.Parameters.AddWithValue("version", version);
            var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CompiledBundle>(json, JsonOptions);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidCatalogName)
        {
            return null;
        }
    }
    private static async Task<long?> GetActiveBundleVersionIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select bundle_version_id from _runtime.active_bundle where id = true", connection, transaction);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task<long> GetBundleVersionIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompiledBundle bundle,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select id
            from _runtime.bundle_versions
            where bundle_id = @bundleId
              and version = @version
              and bundle_hash = @bundleHash
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("bundleId", bundle.BundleId);
        command.Parameters.AddWithValue("version", bundle.Version);
        command.Parameters.AddWithValue("bundleHash", bundle.BundleHash);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            throw new RuntimeBundleVersionIdentityConflictException(
                bundle.BundleId,
                bundle.Version,
                bundle.BundleHash);
        }

        return Convert.ToInt64(result);
    }

    private static async Task EnsureBundleVersionIdentityCompatibleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompiledBundle bundle,
        CancellationToken cancellationToken)
    {
        await using (var tableCommand = new NpgsqlCommand(
            "select to_regclass('_runtime.bundle_versions') is not null",
            connection,
            transaction))
        {
            if (await tableCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return;
            }
        }

        await using var command = new NpgsqlCommand(
            """
            select bundle_hash
            from _runtime.bundle_versions
            where bundle_id = @bundleId and version = @version
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("bundleId", bundle.BundleId);
        command.Parameters.AddWithValue("version", bundle.Version);
        var existingHash = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (existingHash is not null &&
            !existingHash.Equals(bundle.BundleHash, StringComparison.Ordinal))
        {
            throw new RuntimeBundleVersionIdentityConflictException(
                bundle.BundleId,
                bundle.Version,
                bundle.BundleHash,
                existingHash);
        }
    }

    private static async Task<long?> TryInsertRejectedBundleVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompiledBundle bundle,
        string status,
        CancellationToken cancellationToken)
    {
        var metadata = JsonSerializer.Serialize(bundle);
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.bundle_versions (bundle_id, bundle_name, version, bundle_hash, status, artifact_json, compiled_metadata, metadata)
            values (@bundleId, @bundleName, @version, @bundleHash, @status, cast(@metadata as jsonb), cast(@metadata as jsonb), cast(@metadata as jsonb))
            on conflict (bundle_id, version) do nothing
            returning id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("bundleId", bundle.BundleId);
        command.Parameters.AddWithValue("bundleName", bundle.BundleName);
        command.Parameters.AddWithValue("version", bundle.Version);
        command.Parameters.AddWithValue("bundleHash", bundle.BundleHash);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("metadata", metadata);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static object DescribeCandidateBundle(CompiledBundle bundle) => new
    {
        bundle.BundleId,
        bundle.BundleName,
        bundle.Version,
        bundle.BundleHash
    };

    private static async Task UpdateBundleVersionStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long bundleVersionId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update _runtime.bundle_versions
            set status = @status,
                staged_at = case when @status = 'staged' then now() else staged_at end,
                production_at = case when @status = 'production' then now() else production_at end,
                superseded_at = case when @status = 'superseded' then now() else superseded_at end,
                rolled_back_at = case when @status = 'rolled_back' then now() else rolled_back_at end,
                deployed_at = now()
            where id = @bundleVersionId
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("bundleVersionId", bundleVersionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertActiveBundleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long bundleVersionId,
        long? previousBundleVersionId,
        CompiledBundle bundle,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.active_bundle (id, bundle_version_id, previous_bundle_version_id, bundle_id, bundle_version, bundle_hash, activated_at)
            values (true, @bundleVersionId, @previousBundleVersionId, @bundleId, @bundleVersion, @bundleHash, now())
            on conflict (id) do update set
              bundle_version_id = excluded.bundle_version_id,
              previous_bundle_version_id = case
                when _runtime.active_bundle.bundle_version_id = excluded.bundle_version_id
                  then _runtime.active_bundle.previous_bundle_version_id
                else excluded.previous_bundle_version_id
              end,
              bundle_id = excluded.bundle_id,
              bundle_version = excluded.bundle_version,
              bundle_hash = excluded.bundle_hash,
              activated_at = now()
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("bundleVersionId", bundleVersionId);
        command.Parameters.AddWithValue("previousBundleVersionId", previousBundleVersionId is null ? DBNull.Value : previousBundleVersionId.Value);
        command.Parameters.AddWithValue("bundleId", bundle.BundleId);
        command.Parameters.AddWithValue("bundleVersion", bundle.Version);
        command.Parameters.AddWithValue("bundleHash", bundle.BundleHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    private static async Task<RuntimeActiveBundlePointer?> ReadActiveBundlePointerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select bundle_version_id, previous_bundle_version_id, bundle_id, bundle_version, bundle_hash
            from _runtime.active_bundle
            where id = true
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RuntimeActiveBundlePointer(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    private static async Task<CompiledBundle?> ReadBundleByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long bundleVersionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select metadata from _runtime.bundle_versions where id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", bundleVersionId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CompiledBundle>(json, JsonOptions);
    }
}
