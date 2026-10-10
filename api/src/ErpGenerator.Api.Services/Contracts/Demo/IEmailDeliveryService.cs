namespace ErpGenerator.Api.Services.Contracts.Demo;

public interface IEmailDeliveryService
{
    bool IsEnabled { get; }
    Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken);
}
