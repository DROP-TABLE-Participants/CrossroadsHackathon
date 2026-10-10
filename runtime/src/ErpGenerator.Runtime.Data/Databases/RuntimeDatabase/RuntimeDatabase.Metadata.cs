using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    public async Task<RuntimeMetadata> ReadMetadataAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);

        var entities = new List<JsonElement>();
        await using (var command = new NpgsqlCommand("select metadata from _runtime.entity_definitions order by id", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                entities.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.Clone());
            }
        }

        var views = new List<JsonElement>();
        await using (var command = new NpgsqlCommand("select metadata from _runtime.view_definitions order by id", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                views.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.Clone());
            }
        }

        var workflows = new List<JsonElement>();
        await using (var command = new NpgsqlCommand("select metadata from _runtime.workflow_definitions order by id", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                workflows.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.Clone());
            }
        }

        var actions = new List<JsonElement>();
        try
        {
            await using var command = new NpgsqlCommand("select metadata from _runtime.action_definitions order by id", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                actions.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.Clone());
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Older tenant databases did not have declarative business actions yet.
        }

        var reports = new List<JsonElement>();
        await using (var command = new NpgsqlCommand("select metadata from _runtime.report_definitions order by id", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                reports.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.Clone());
            }
        }

        JsonElement? dashboard = null;
        await using (var command = new NpgsqlCommand("select metadata from _runtime.dashboard_definitions where id = 'main'", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                dashboard = JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
            }
        }

        string? bundleId = null;
        string? bundleVersion = null;
        string? bundleHash = null;
        try
        {
            await using var command = new NpgsqlCommand(
                """
                select bv.bundle_id, bv.version, bv.bundle_hash
                from _runtime.active_bundle ab
                join _runtime.bundle_versions bv on bv.id = ab.bundle_version_id
                where ab.id = true
                limit 1
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                bundleId = reader.GetString(0);
                bundleVersion = reader.GetString(1);
                bundleHash = reader.GetString(2);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Older tenant databases did not have an explicit active bundle pointer.
        }

        if (bundleId is null)
        {
            await using var command = new NpgsqlCommand(
                """
                select bundle_id, version, bundle_hash
                from _runtime.bundle_versions
                order by deployed_at desc
                limit 1
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                bundleId = reader.GetString(0);
                bundleVersion = reader.GetString(1);
                bundleHash = reader.GetString(2);
            }
        }

        return new RuntimeMetadata(
            tenant.TenantSlug,
            tenant.Host,
            tenant.Environment.ToString(),
            bundleId,
            bundleVersion,
            bundleHash,
            entities,
            views,
            workflows,
            actions,
            reports,
            dashboard);
    }
}
