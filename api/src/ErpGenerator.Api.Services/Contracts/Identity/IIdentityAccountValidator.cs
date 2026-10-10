using System.Security.Claims;

namespace ErpGenerator.Api.Services.Contracts.Identity;

public interface IIdentityAccountValidator
{
    Task<bool> IsActiveCustomerAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<bool> IsActiveAdministratorAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}
