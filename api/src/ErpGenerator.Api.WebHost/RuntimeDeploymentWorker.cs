
namespace ErpGenerator.Api.WebHost;

public sealed class RuntimeDeploymentWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<RuntimeDeploymentWorker> logger) : BackgroundService
{
    private readonly string workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IGeneratedErpDeploymentQueue>();
                if (await queue.TryQueueNextAsync(stoppingToken))
                    continue;
                var processor = scope.ServiceProvider.GetRequiredService<IRuntimeDeploymentProcessor>();
                if (!await processor.TryProcessNextAsync(workerId, stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Runtime deployment worker failed; it will retry.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
