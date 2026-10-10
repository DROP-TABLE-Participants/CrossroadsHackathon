namespace ErpGenerator.Api.Services.Contracts.Admin;

public interface IAdminOverviewService
{
    Task<AdminOverviewViewModel> GetAsync(CancellationToken cancellationToken);
}
