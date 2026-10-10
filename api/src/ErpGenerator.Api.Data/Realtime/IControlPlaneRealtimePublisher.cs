using ErpGenerator.Api.Data.Context;

namespace ErpGenerator.Api.Data.Realtime;

public interface IControlPlaneRealtimePublisher
{
    Task PublishAsync(
        ErpGeneratorDbContext dbContext,
        IReadOnlyCollection<ControlPlaneProjectChange> changes,
        CancellationToken cancellationToken);
}
