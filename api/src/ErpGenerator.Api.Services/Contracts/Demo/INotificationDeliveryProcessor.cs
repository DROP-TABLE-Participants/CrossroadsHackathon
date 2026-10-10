namespace ErpGenerator.Api.Services.Contracts.Demo;

public interface INotificationDeliveryProcessor
{
    Task<bool> TryProcessNextAsync(CancellationToken cancellationToken);
}
