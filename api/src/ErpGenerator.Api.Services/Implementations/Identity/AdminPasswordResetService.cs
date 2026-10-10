using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Identity;

public sealed class AdminPasswordResetService(
    ErpGeneratorDbContext controlPlane,
    UserManager<AdminAccount> userManager,
    IAdminSessionService sessions,
    IEmailDeliveryService emailDelivery,
    IOptions<PortalOptions> portalOptions,
    ILogger<AdminPasswordResetService> logger) : IAdminPasswordResetService
{
    private const string ResetTokenPrefix = "administrator:";

    public async Task RequestAsync(
        RequestAdminPasswordResetInputModel input,
        CancellationToken cancellationToken)
    {
        var email = input.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal)) return;
        var admin = await userManager.FindByEmailAsync(email);
        if (admin is null || !admin.IsActive || !emailDelivery.IsEnabled) return;

        try
        {
            var token = ResetTokenPrefix + await userManager.GeneratePasswordResetTokenAsync(admin);
            controlPlane.AdminAuditRecords.Add(new AdminAuditRecord
            {
                ActorAdminAccountId = admin.Id,
                ActorEmailSnapshot = email,
                Action = "administrator_password_reset_requested",
                ResourceType = "admin_account",
                ResourceId = admin.Id.ToString()
            });
            await controlPlane.SaveChangesAsync(cancellationToken);
            var url = PasswordResetUrlFactory.Create(
                portalOptions.Value.PublicBaseUrl,
                input.Locale,
                "admin/reset-password",
                email,
                token);
            await emailDelivery.SendAsync(
                email,
                "Reset your ErpGenerator administrator password",
                $"Use this link to reset your ErpGenerator administrator password:\n\n{url}\n\nIf you did not request this, contact another administrator.",
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Administrator password-reset delivery failed.");
        }
    }

    public async Task ResetAsync(ResetAdminPasswordInputModel input, CancellationToken cancellationToken)
    {
        var email = input.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var admin = await userManager.FindByEmailAsync(email);
        if (admin is null || !admin.IsActive)
            throw new CustomerValidationException("The password-reset link is invalid or expired.");
        var token = input.Token ?? string.Empty;
        if (!token.StartsWith(ResetTokenPrefix, StringComparison.Ordinal))
            throw new CustomerValidationException("The password-reset link is invalid or expired.");
        var reset = await userManager.ResetPasswordAsync(
            admin,
            token[ResetTokenPrefix.Length..],
            input.NewPassword ?? string.Empty);
        if (!reset.Succeeded)
            throw new CustomerValidationException(ResetFailure(reset));
        await userManager.UpdateSecurityStampAsync(admin);
        await sessions.RevokeAllAsync(admin.Id, cancellationToken);
        try
        {
            controlPlane.AdminAuditRecords.Add(new AdminAuditRecord
            {
                ActorAdminAccountId = admin.Id,
                ActorEmailSnapshot = email,
                Action = "administrator_password_reset_completed",
                ResourceType = "admin_account",
                ResourceId = admin.Id.ToString()
            });
            await controlPlane.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Administrator password reset completed, but its audit record could not be persisted for {AdminAccountId}.",
                admin.Id);
        }
    }

    private static string ResetFailure(IdentityResult result) =>
        result.Errors.Any(value => value.Code.Contains("Token", StringComparison.OrdinalIgnoreCase))
            ? "The password-reset link is invalid or expired."
            : string.Join(" ", result.Errors.Select(value => value.Description));
}
