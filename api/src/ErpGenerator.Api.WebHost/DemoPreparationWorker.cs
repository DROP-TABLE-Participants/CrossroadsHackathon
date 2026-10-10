namespace ErpGenerator.Api.WebHost;

public sealed class DemoPreparationWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<DemoPreparationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDemoWorkspaceService>().PrepareAsync(configuration.GetValue<int>("Demo:PreparedCopies"), stoppingToken);
        await Task.WhenAll(ProcessAsync(stoppingToken), ProcessAsync(stoppingToken));
    }
    private async Task ProcessAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<IDemoWorkspaceService>().ProcessNextAsync(ct)) continue;
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogWarning(exception, "Demo preparation will retry."); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
