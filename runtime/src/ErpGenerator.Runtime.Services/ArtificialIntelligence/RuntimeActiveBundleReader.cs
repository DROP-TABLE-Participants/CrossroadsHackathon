using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.ArtificialIntelligence;

public sealed class RuntimeActiveBundleReader(ITenantDatabaseConnectionFactory connectionFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CompiledBundle> ReadAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select coalesce(bundle.compiled_metadata, bundle.metadata)::text
            from _runtime.active_bundle active
            join _runtime.bundle_versions bundle on bundle.id = active.bundle_version_id
            where active.id = true
            limit 1
            """;
        var metadata = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(metadata))
        {
            throw new InvalidOperationException("An active compiled ERP bundle is required.");
        }

        try
        {
            return JsonSerializer.Deserialize<CompiledBundle>(metadata, JsonOptions) ??
                   throw new JsonException("Compiled bundle metadata was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The active compiled ERP bundle metadata is invalid.", exception);
        }
    }
}
