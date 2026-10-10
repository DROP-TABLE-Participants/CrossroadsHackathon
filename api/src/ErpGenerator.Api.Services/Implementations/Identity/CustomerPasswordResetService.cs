using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class CustomerPasswordResetService(
    UserManager<CustomerAccount> userManager,
    ICustomerSessionService sessions,
    IEmailDeliveryService emailDelivery,
    IOptions<PortalOptions> portalOptions,
    ILogger<CustomerPasswordResetService> logger) : ICustomerPasswordResetService
{
    private const string ResetTokenPrefix = "customer:";

    public async Task RequestAsync(
        RequestCustomerPasswordResetInputModel input,
        CancellationToken cancellationToken)
    {
        var email = input.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal)) return;
        var customer = await userManager.FindByEmailAsync(email);
        if (customer is null || !customer.IsActive || !emailDelivery.IsEnabled) return;

        try
        {
            var token = ResetTokenPrefix + await userManager.GeneratePasswordResetTokenAsync(customer);
            var url = PasswordResetUrlFactory.Create(
                portalOptions.Value.PublicBaseUrl,
                input.Locale,
                "reset-password",
                email,
                token);
            await emailDelivery.SendAsync(
                email,
                "Reset your ErpGenerator password",
                $"Use this link to reset your ErpGenerator customer password:\n\n{url}\n\nIf you did not request this, ignore this email.",
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Customer password-reset delivery failed.");
        }
    }

    public async Task ResetAsync(ResetCustomerPasswordInputModel input, CancellationToken cancellationToken)
    {
        var email = input.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var customer = await userManager.FindByEmailAsync(email);
        if (customer is null || !customer.IsActive)
            throw new CustomerValidationException("The password-reset link is invalid or expired.");
        var token = input.Token ?? string.Empty;
        if (!token.StartsWith(ResetTokenPrefix, StringComparison.Ordinal))
            throw new CustomerValidationException("The password-reset link is invalid or expired.");
        var reset = await userManager.ResetPasswordAsync(
            customer,
            token[ResetTokenPrefix.Length..],
            input.NewPassword ?? string.Empty);
        if (!reset.Succeeded)
            throw new CustomerValidationException(ResetFailure(reset));
        await userManager.UpdateSecurityStampAsync(customer);
        await sessions.RevokeAllAsync(customer.Id, cancellationToken);
    }

    private static string ResetFailure(IdentityResult result) =>
        result.Errors.Any(value => value.Code.Contains("Token", StringComparison.OrdinalIgnoreCase))
            ? "The password-reset link is invalid or expired."
            : string.Join(" ", result.Errors.Select(value => value.Description));
}
