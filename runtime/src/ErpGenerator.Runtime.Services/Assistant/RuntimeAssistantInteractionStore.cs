using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.ArtificialIntelligence;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Assistant;

public sealed class RuntimeAssistantInteractionStore(ITenantDatabaseConnectionFactory connectionFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task StartAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        Guid interactionId,
        RuntimeAssistantContext context,
        string question,
        string model,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenIfAvailableAsync(tenant, cancellationToken);
        if (connection is null)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into _runtime.assistant_interactions (
              id, runtime_user_id, environment, bundle_hash, context, question,
              model, status, correlation_id)
            values (
              @id, @runtimeUserId, @environment, @bundleHash, @context, @question,
              @model, 'running', @correlationId)
            """;
        command.Parameters.AddWithValue("id", interactionId);
        command.Parameters.AddWithValue("runtimeUserId", user.UserId);
        command.Parameters.AddWithValue("environment", tenant.Environment.ToString());
        command.Parameters.AddWithValue("bundleHash", context.BundleHash);
        command.Parameters.Add("context", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(context, JsonOptions);
        command.Parameters.AddWithValue("question", question);
        command.Parameters.AddWithValue("model", model);
        command.Parameters.AddWithValue("correlationId", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        ResolvedTenantEnvironment tenant,
        Guid interactionId,
        string answer,
        IReadOnlyList<RuntimeEvidenceReference> evidence,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenIfAvailableAsync(tenant, cancellationToken);
        if (connection is null)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update _runtime.assistant_interactions
            set answer = @answer,
                evidence = @evidence,
                status = 'completed',
                completed_at = now()
            where id = @id
            """;
        command.Parameters.AddWithValue("id", interactionId);
        command.Parameters.AddWithValue("answer", answer);
        command.Parameters.Add("evidence", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(evidence, JsonOptions);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FailAsync(
        ResolvedTenantEnvironment tenant,
        Guid interactionId,
        string failureCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenIfAvailableAsync(tenant, cancellationToken);
        if (connection is null)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update _runtime.assistant_interactions
            set status = 'failed',
                failure_code = @failureCode,
                completed_at = now()
            where id = @id
            """;
        command.Parameters.AddWithValue("id", interactionId);
        command.Parameters.AddWithValue("failureCode", failureCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection?> OpenIfAvailableAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select to_regclass('_runtime.assistant_interactions') is not null";
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            await connection.DisposeAsync();
            return null;
        }

        return connection;
    }
}
