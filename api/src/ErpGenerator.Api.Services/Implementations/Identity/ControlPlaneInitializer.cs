using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class ControlPlaneInitializer(
    ErpGeneratorDbContext dbContext,
    RoleManager<IdentityRole<Guid>> roleManager,
    UserManager<CustomerAccount> userManager) : IControlPlaneInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);
        const string customerRoleName = "Customer";
        if (!await roleManager.RoleExistsAsync(customerRoleName))
        {
            var created = await roleManager.CreateAsync(new IdentityRole<Guid>(customerRoleName));
            if (!created.Succeeded)
                throw new InvalidOperationException("Unable to initialize the Customer role.");
        }

        const string legacyAdminRoleName = "Admin";
        if (!await roleManager.RoleExistsAsync(legacyAdminRoleName)) return;
        var legacyAdmins = await userManager.GetUsersInRoleAsync(legacyAdminRoleName);
        var now = DateTimeOffset.UtcNow;
        foreach (var account in legacyAdmins)
        {
            var isCustomer = await userManager.IsInRoleAsync(account, customerRoleName);
            if (!isCustomer) account.IsActive = false;
            var activeSessions = await dbContext.RefreshSessions
                .Where(value => value.CustomerAccountId == account.Id && value.RevokedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var session in activeSessions) session.RevokedAt = now;
            var removed = await userManager.RemoveFromRoleAsync(account, legacyAdminRoleName);
            if (!removed.Succeeded)
                throw new InvalidOperationException("Unable to remove the legacy administrator role.");
            if (!isCustomer)
            {
                var updated = await userManager.UpdateAsync(account);
                if (!updated.Succeeded)
                    throw new InvalidOperationException("Unable to deactivate the legacy administrator account.");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var legacyRole = await roleManager.FindByNameAsync(legacyAdminRoleName);
        if (legacyRole is not null)
        {
            var deleted = await roleManager.DeleteAsync(legacyRole);
            if (!deleted.Succeeded)
                throw new InvalidOperationException("Unable to remove the legacy administrator role.");
        }
    }
}
