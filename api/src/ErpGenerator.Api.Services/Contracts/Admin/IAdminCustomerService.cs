namespace ErpGenerator.Api.Services.Contracts.Admin;

public interface IAdminCustomerService
{
    Task<AdminCustomerListViewModel> ListAsync(
        string? search,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
    Task<AdminCustomerDetailsViewModel?> GetAsync(Guid customerId, CancellationToken cancellationToken);
    Task<AdminCustomerViewModel> SetStatusAsync(
        Guid actorId,
        string actorEmail,
        Guid customerId,
        UpdateAdminCustomerStatusInputModel input,
        CancellationToken cancellationToken);
    Task RequestPasswordResetAsync(
        Guid actorId,
        string actorEmail,
        Guid customerId,
        string? locale,
        CancellationToken cancellationToken);
}
