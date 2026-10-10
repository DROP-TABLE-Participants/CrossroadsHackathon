namespace ErpGenerator.Api.WebHost;

public sealed class ConnectorRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ConnectorRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sources = scope.ServiceProvider.GetRequiredService<INangoInputSourceService>();
                var removed = await sources.PurgeExpiredContentAsync(stoppingToken);
                if (removed > 0)
                    logger.LogInformation("Purged {RecordCount} expired connector records.", removed);
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Connector retention cleanup failed; it will retry.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}
