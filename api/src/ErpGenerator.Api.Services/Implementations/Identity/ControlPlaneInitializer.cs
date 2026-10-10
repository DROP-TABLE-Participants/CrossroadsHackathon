using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class ControlPlaneInitializer(
    ErpGeneratorDbContext dbContext,
    RoleManager<IdentityRole<Guid>> roleManager) : IControlPlaneInitializer
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

    }
}
