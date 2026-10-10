using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class AdminIdentityInitializer(
    AdminIdentityDbContext dbContext,
    RoleManager<AdminRole> roleManager,
    UserManager<AdminAccount> userManager,
    IOptions<AdminBootstrapOptions> bootstrapOptions) : IAdminIdentityInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);
        if (!await roleManager.RoleExistsAsync(IdentityRoleNames.PlatformAdministrator))
        {
            var roleCreated = await roleManager.CreateAsync(new AdminRole { Name = IdentityRoleNames.PlatformAdministrator });
            if (!roleCreated.Succeeded)
                throw new InvalidOperationException("Unable to initialize the platform administrator role.");
        }

        var settings = bootstrapOptions.Value;
        if (settings.Enabled)
        {
            if (string.IsNullOrWhiteSpace(settings.Email) || string.IsNullOrWhiteSpace(settings.Password))
            {
                throw new InvalidOperationException(
                    "Enabled admin bootstrap requires AdminBootstrap__Email and AdminBootstrap__Password.");
            }

            var email = settings.Email.Trim().ToLowerInvariant();
            var admin = await userManager.FindByEmailAsync(email);
            if (admin is null)
            {
                admin = new AdminAccount
                {
                    Id = Guid.NewGuid(),
                    UserName = email,
                    Email = email,
                    Name = string.IsNullOrWhiteSpace(settings.Name)
                        ? "ErpGenerator administrator"
                        : settings.Name.Trim(),
                    EmailConfirmed = true,
                    IsActive = true
                };
                var created = await userManager.CreateAsync(admin, settings.Password);
                if (!created.Succeeded)
                    throw new InvalidOperationException("Unable to initialize the configured administrator account.");
            }

            if (!await userManager.IsInRoleAsync(admin, IdentityRoleNames.PlatformAdministrator))
            {
                var assigned = await userManager.AddToRoleAsync(admin, IdentityRoleNames.PlatformAdministrator);
                if (!assigned.Succeeded)
                    throw new InvalidOperationException("Unable to assign the platform administrator role.");
            }
        }

        var platformAdministrators = await userManager.GetUsersInRoleAsync(
            IdentityRoleNames.PlatformAdministrator);
        if (!platformAdministrators.Any(value => value.IsActive))
            throw new InvalidOperationException(
                "Administrator identity migration requires at least one active platform administrator. " +
                "Enable the one-time AdminBootstrap configuration before retrying.");
    }
}
