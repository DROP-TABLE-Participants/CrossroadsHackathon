using Microsoft.AspNetCore.Identity;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class AdminPasswordPolicyValidator : IPasswordValidator<AdminAccount>
{
    public Task<IdentityResult> ValidateAsync(
        UserManager<AdminAccount> manager,
        AdminAccount user,
        string? password) =>
        Task.FromResult(password?.Length >= 12
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError
            {
                Code = "AdminPasswordTooShort",
                Description = "Administrator passwords must be at least 12 characters."
            }));
}
