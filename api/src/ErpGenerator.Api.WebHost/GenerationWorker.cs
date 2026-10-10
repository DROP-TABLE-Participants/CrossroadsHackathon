
namespace ErpGenerator.Api.WebHost;

public sealed class GenerationWorker(
    IServiceScopeFactory scopeFactory,
    Microsoft.Extensions.Options.IOptions<GenerationOrchestrationOptions> options,
    ILogger<GenerationWorker> logger) : BackgroundService
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
                var queuedGeneration = scope.ServiceProvider.GetRequiredService<IQueuedGenerationService>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IGenerationRunOrchestrator>();
                await queuedGeneration.TryResumeNextReadyAsync(stoppingToken);
                await queuedGeneration.TryStartNextReadyAsync(stoppingToken);
                if (!await orchestrator.TryProcessNextAsync(workerId, stoppingToken))
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(Math.Clamp(
                            options.Value.WorkerIdleDelayMilliseconds,
                            100,
                            60_000)),
                        stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Generation worker loop failed; it will retry.");
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Clamp(
                        options.Value.WorkerFailureDelayMilliseconds,
                        100,
                        60_000)),
                    stoppingToken);
            }
        }
    }
}
