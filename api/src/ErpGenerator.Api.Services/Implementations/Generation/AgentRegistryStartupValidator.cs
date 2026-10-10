using Microsoft.Extensions.Hosting;

namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed class AgentRegistryStartupValidator(
    IAgentDefinitionRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = registry.All;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
