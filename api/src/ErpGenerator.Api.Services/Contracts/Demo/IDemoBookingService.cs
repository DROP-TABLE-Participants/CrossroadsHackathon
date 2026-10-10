namespace ErpGenerator.Api.Services.Contracts.Demo;

public interface IDemoBookingService
{
    Task<DemoBookingAvailabilityViewModel> AvailabilityAsync(CancellationToken cancellationToken);
    Task<DemoBookingViewModel> CreateAsync(Guid customerId, CreateDemoBookingInputModel input, CancellationToken cancellationToken);
}
