using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using ErpGenerator.Api.Services.Contracts.Realtime;

namespace ErpGenerator.Api.Services.Implementations.Realtime;

public sealed class PostgresRealtimeListener(
    IConfiguration configuration,
    IProjectRealtimeEventSink eventSink,
    ILogger<PostgresRealtimeListener> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString =
        configuration.GetConnectionString("ControlPlane")
        ?? throw new InvalidOperationException("ConnectionStrings__ControlPlane is required.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
                retryDelay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Realtime database listener disconnected. Reconnecting in {RetryDelay}.",
                    retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var messages = new ConcurrentQueue<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        connection.Notification += (_, args) =>
        {
            if (string.Equals(
                    args.Channel,
                    PostgresControlPlaneRealtimePublisher.Channel,
                    StringComparison.Ordinal))
            {
                messages.Enqueue(args.Payload);
            }
        };

        await connection.OpenAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
                         $"LISTEN {PostgresControlPlaneRealtimePublisher.Channel};",
                         connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation("Realtime database listener is ready.");
        while (!cancellationToken.IsCancellationRequested)
        {
            await connection.WaitAsync(cancellationToken);
            while (messages.TryDequeue(out var payload))
            {
                await BroadcastAsync(payload, cancellationToken);
            }
        }
    }

    private async Task BroadcastAsync(string payload, CancellationToken cancellationToken)
    {
        ProjectRealtimeEvent? message;
        try
        {
            message = JsonSerializer.Deserialize<ProjectRealtimeEvent>(payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Ignored an invalid realtime database notification.");
            return;
        }

        if (message is null ||
            message.CustomerId == Guid.Empty ||
            message.ProjectId == Guid.Empty ||
            message.EventName is not ("projectChanged" or "projectRemoved"))
        {
            logger.LogWarning("Ignored an incomplete realtime database notification.");
            return;
        }

        await eventSink.SendAsync(message, cancellationToken);
    }
}
