namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface ICustomerFeedbackService
{
    Task<ErpFeedbackListViewModel> ListFeedbackAsync(Guid customerId, CancellationToken cancellationToken);
    Task<ErpUsageObservationListViewModel> ListUsageAsync(Guid customerId, CancellationToken cancellationToken);
    Task<ErpUsageObservationViewModel> DismissUsageAsync(Guid customerId, Guid observationId, CancellationToken cancellationToken);
}
