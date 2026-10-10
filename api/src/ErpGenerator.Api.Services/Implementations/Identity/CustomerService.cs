using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Services.Implementations.Demo;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class CustomerService(
    ErpGeneratorDbContext dbContext,
    UserManager<CustomerAccount> userManager,
    ICustomerSessionService sessions) : ICustomerService
{
    private const string CustomerRole = "Customer";

    public async Task<CustomerAuthenticationViewModel> CreateGuestAsync(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var email = $"guest-{id:N}@demo.invalid";
        var customer = new CustomerAccount
        {
            Id = id, UserName = email, Email = email, Name = "Guest",
            RepresentativeName = "Guest", IsGuest = true, IsActive = true
        };
        var created = await userManager.CreateAsync(customer);
        if (!created.Succeeded) throw RegistrationFailure(created);
        var role = await userManager.AddToRoleAsync(customer, CustomerRole);
        if (!role.Succeeded)
        {
            await userManager.DeleteAsync(customer);
            throw new InvalidOperationException("Guest role initialization failed.");
        }
        return await sessions.CreateAsync(customer.Id, cancellationToken);
    }

    public async Task<CustomerAuthenticationViewModel> RegisterAsync(
        RegisterCustomerInputModel input,
        CancellationToken cancellationToken)
    {
        var name = RequireText(input.Name, "Name", 120);
        var email = NormalizeEmail(input.Email);
        var password = input.Password ?? string.Empty;
        ValidatePassword(password);

        if (await userManager.FindByEmailAsync(email) is not null)
            throw new CustomerConflictException("Unable to create account with this email.");

        var customer = new CustomerAccount
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            Name = name,
            RepresentativeName = name,
            PhoneNumber = OptionalText(input.PhoneNumber, 50),
            CompanyName = OptionalText(input.CompanyName, 160),
            Position = OptionalText(input.Position, 120),
            IsActive = true
        };
        var created = await userManager.CreateAsync(customer, password);
        if (!created.Succeeded) throw RegistrationFailure(created);

        var addedToRole = await userManager.AddToRoleAsync(customer, CustomerRole);
        if (!addedToRole.Succeeded)
        {
            await userManager.DeleteAsync(customer);
            throw new InvalidOperationException("Customer role initialization failed.");
        }

        return await sessions.CreateAsync(customer.Id, cancellationToken);
    }

    public async Task<CustomerAuthenticationViewModel> LoginAsync(
        LoginCustomerInputModel input,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(input.Email);
        var password = input.Password ?? string.Empty;
        var customer = await userManager.FindByEmailAsync(email);
        if (customer is null || !customer.IsActive || await userManager.IsLockedOutAsync(customer))
            throw new CustomerAuthenticationException("Invalid login credentials.");

        if (!await userManager.CheckPasswordAsync(customer, password))
        {
            await userManager.AccessFailedAsync(customer);
            throw new CustomerAuthenticationException("Invalid login credentials.");
        }

        await userManager.ResetAccessFailedCountAsync(customer);
        return await sessions.CreateAsync(customer.Id, cancellationToken);
    }

    public Task<CustomerAuthenticationViewModel> RefreshAsync(
        RefreshCustomerSessionInputModel input,
        CancellationToken cancellationToken) =>
        sessions.RefreshAsync(input.RefreshToken?.Trim() ?? string.Empty, cancellationToken);

    public Task LogoutAsync(LogoutCustomerSessionInputModel input, CancellationToken cancellationToken) =>
        sessions.RevokeAsync(input.RefreshToken?.Trim(), cancellationToken);

    public async Task<CurrentCustomerViewModel?> GetCurrentAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await dbContext.CustomerAccounts.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == customerId && value.IsActive,
            cancellationToken);
        if (customer is null) return null;

        var booking = await dbContext.DemoBookings.AsNoTracking()
            .Where(value => value.CustomerId == customerId && value.Status != "cancelled")
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var roles = await userManager.GetRolesAsync(customer);
        return new CurrentCustomerViewModel(
            CustomerViewModelFactory.Create(customer, roles.FirstOrDefault() ?? CustomerRole),
            booking is null ? null : DemoBookingService.Map(booking));
    }

    public async Task<CurrentCustomerViewModel> UpdateAsync(
        Guid customerId,
        UpdateCustomerInputModel input,
        CancellationToken cancellationToken)
    {
        var customer = await userManager.FindByIdAsync(customerId.ToString())
            ?? throw new CustomerAuthenticationException("Customer account was not found.");
        if (!customer.IsActive) throw new CustomerAuthenticationException("Customer account was not found.");

        var email = NormalizeEmail(input.Email);
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null && existing.Id != customerId)
            throw new CustomerConflictException("Unable to use this email.");

        customer.Name = RequireText(input.Name, "Name", 120);
        customer.RepresentativeName = customer.Name;
        customer.Email = email;
        customer.NormalizedEmail = userManager.NormalizeEmail(email);
        customer.UserName = email;
        customer.NormalizedUserName = userManager.NormalizeName(email);
        customer.PhoneNumber = RequireText(input.PhoneNumber, "Phone number", 50);
        customer.CompanyName = RequireText(input.CompanyName, "Company name", 160);
        customer.Position = OptionalText(input.Position, 120);
        var updated = await userManager.UpdateAsync(customer);
        if (!updated.Succeeded) throw UpdateFailure(updated);
        return (await GetCurrentAsync(customerId, cancellationToken))!;
    }

    private static Exception RegistrationFailure(IdentityResult result)
    {
        if (result.Errors.Any(value => value.Code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)))
            return new CustomerConflictException("Unable to create account with this email.");
        return new CustomerValidationException(string.Join(" ", result.Errors.Select(value => value.Description)));
    }

    private static Exception UpdateFailure(IdentityResult result)
    {
        if (result.Errors.Any(value => value.Code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)))
            return new CustomerConflictException("Unable to use this email.");
        return new CustomerValidationException(string.Join(" ", result.Errors.Select(value => value.Description)));
    }

    private static string NormalizeEmail(string? value)
    {
        var email = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (email.Length is < 3 or > 320 || !email.Contains('@', StringComparison.Ordinal))
            throw new CustomerValidationException("Please provide a valid email address.");
        return email;
    }

    private static string RequireText(string? value, string label, int maximumLength)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length == 0) throw new CustomerValidationException($"{label} is required.");
        if (result.Length > maximumLength) throw new CustomerValidationException($"{label} exceeds allowed length.");
        return result;
    }

    private static string? OptionalText(string? value, int maximumLength)
    {
        var result = value?.Trim();
        if (string.IsNullOrEmpty(result)) return null;
        if (result.Length > maximumLength) throw new CustomerValidationException("One or more fields exceed allowed length.");
        return result;
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length < 10 ||
            !password.Any(char.IsLower) ||
            !password.Any(char.IsUpper) ||
            !password.Any(char.IsDigit) ||
            !password.Any(value => !char.IsLetterOrDigit(value)))
        {
            throw new CustomerValidationException(
                "Password must be at least 10 characters and include lowercase, uppercase, a number, and a special character.");
        }
    }
}
