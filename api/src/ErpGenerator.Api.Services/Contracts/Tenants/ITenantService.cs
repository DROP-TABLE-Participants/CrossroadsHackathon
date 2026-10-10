namespace ErpGenerator.Api.Services.Contracts.Tenants;

public interface ITenantService
{
    Task<TenantListViewModel> ListAsync(Guid ownerId, CancellationToken cancellationToken);
    Task<CreateTenantViewModel> CreateAsync(Guid ownerId, CreateTenantInputModel input, CancellationToken cancellationToken);
}
