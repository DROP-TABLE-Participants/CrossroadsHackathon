using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class IdentityAccountValidator(
    ErpGeneratorDbContext controlPlane) : IIdentityAccountValidator
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
