using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class IdentityAccountValidator(
    ErpGeneratorDbContext controlPlane,
    AdminIdentityDbContext adminIdentity) : IIdentityAccountValidator
{
    public Task<bool> IsActiveCustomerAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        IsActiveAsync(
            principal,
            IdentityClaimNames.CustomerRealm,
            (id, securityStamp) => controlPlane.CustomerAccounts.AnyAsync(
                value => value.Id == id &&
                         value.IsActive &&
                         value.SecurityStamp == securityStamp,
                cancellationToken));

    public Task<bool> IsActiveAdministratorAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        IsActiveAsync(
            principal,
            IdentityClaimNames.AdministratorRealm,
            (id, securityStamp) =>
                (from account in adminIdentity.AdminAccounts
                 join userRole in adminIdentity.UserRoles on account.Id equals userRole.UserId
                 join role in adminIdentity.Roles on userRole.RoleId equals role.Id
                 where account.Id == id &&
                       account.IsActive &&
                       account.SecurityStamp == securityStamp &&
                       role.NormalizedName == "PLATFORMADMINISTRATOR"
                 select account.Id).AnyAsync(cancellationToken));

    private static Task<bool> IsActiveAsync(
        ClaimsPrincipal principal,
        string requiredRealm,
        Func<Guid, string, Task<bool>> accountExists)
    {
        if (!string.Equals(
                principal.FindFirstValue(IdentityClaimNames.AccountRealm),
                requiredRealm,
                StringComparison.Ordinal))
            return Task.FromResult(false);
        var securityStamp = principal.FindFirstValue(IdentityClaimNames.SecurityStamp);
        return Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) &&
               !string.IsNullOrWhiteSpace(securityStamp)
            ? accountExists(id, securityStamp)
            : Task.FromResult(false);
    }
}
