using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Admin;

public sealed class AdminAccountManagementService(
    AdminIdentityDbContext adminIdentity,
    ErpGeneratorDbContext controlPlane,
    UserManager<AdminAccount> userManager,
    IAdminSessionService sessions,
    IAdminPasswordResetService passwordReset) : IAdminAccountManagementService
{
    public async Task<AdminAdministratorListViewModel> ListAsync(
        string? search,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = AdminQueryRules.Page(page);
        pageSize = AdminQueryRules.PageSize(pageSize);
        var query = adminIdentity.AdminAccounts.AsNoTracking();
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(value =>
                (value.Email != null && EF.Functions.ILike(value.Email, $"%{term}%")) ||
                EF.Functions.ILike(value.Name, $"%{term}%"));
        }
        if (isActive is not null) query = query.Where(value => value.IsActive == isActive);

        var totalCount = await query.CountAsync(cancellationToken);
        var administrators = await query.OrderByDescending(value => value.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(value => new AdminAdministratorViewModel(
                value.Id,
                value.Email ?? string.Empty,
                value.Name,
                value.IsActive,
                IdentityRoleNames.PlatformAdministrator,
                value.CreatedAt,
                value.UpdatedAt))
            .ToListAsync(cancellationToken);
        return new AdminAdministratorListViewModel(administrators, page, pageSize, totalCount);
    }

    public async Task<AdminAdministratorViewModel> CreateAsync(
        Guid actorId,
        string actorEmail,
        CreateAdminAdministratorInputModel input,
        CancellationToken cancellationToken)
    {
        var email = input.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var name = input.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new ArgumentException("Administrator name is required and must be 120 characters or fewer.");
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal) || email.Length > 320)
            throw new ArgumentException("A valid administrator email is required.");
        if (await userManager.FindByEmailAsync(email) is not null)
            throw new InvalidOperationException("An administrator with this email already exists.");

        var administrator = new AdminAccount
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            Name = name,
            IsActive = true
        };
        var created = await userManager.CreateAsync(administrator);
        EnsureSucceeded(created, "The administrator could not be created.");
        var assigned = await userManager.AddToRoleAsync(administrator, IdentityRoleNames.PlatformAdministrator);
        if (!assigned.Succeeded)
        {
            await userManager.DeleteAsync(administrator);
            EnsureSucceeded(assigned, "The administrator role could not be assigned.");
        }

        Audit(
            actorId,
            actorEmail,
            "administrator_created",
            administrator.Id,
            new { administrator.Email, administrator.Name });
        await controlPlane.SaveChangesAsync(cancellationToken);
        await passwordReset.RequestAsync(
            new RequestAdminPasswordResetInputModel(administrator.Email, input.Locale),
            cancellationToken);
        return Map(administrator);
    }

    public async Task<AdminAdministratorViewModel> SetStatusAsync(
        Guid actorId,
        string actorEmail,
        Guid administratorId,
        UpdateAdminAdministratorStatusInputModel input,
        CancellationToken cancellationToken)
    {
        var administrator = await adminIdentity.AdminAccounts.SingleOrDefaultAsync(
            value => value.Id == administratorId,
            cancellationToken)
            ?? throw new KeyNotFoundException("Administrator account was not found.");
        if (!input.IsActive && actorId == administratorId)
            throw new InvalidOperationException("You cannot deactivate your own administrator account.");
        if (!input.IsActive &&
            await adminIdentity.AdminAccounts.CountAsync(value => value.IsActive, cancellationToken) <= 1)
        {
            throw new InvalidOperationException("The last active administrator cannot be deactivated.");
        }

        administrator.IsActive = input.IsActive;
        await adminIdentity.SaveChangesAsync(cancellationToken);
        if (!input.IsActive) await sessions.RevokeAllAsync(administratorId, cancellationToken);
        Audit(
            actorId,
            actorEmail,
            "administrator_status_changed",
            administrator.Id,
            new { input.IsActive, administrator.Email });
        await controlPlane.SaveChangesAsync(cancellationToken);
        return Map(administrator);
    }

    public async Task RequestPasswordResetAsync(
        Guid actorId,
        string actorEmail,
        Guid administratorId,
        string? locale,
        CancellationToken cancellationToken)
    {
        var administrator = await adminIdentity.AdminAccounts.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == administratorId, cancellationToken)
            ?? throw new KeyNotFoundException("Administrator account was not found.");
        if (!administrator.IsActive)
            throw new InvalidOperationException("Activate the administrator before sending a password reset.");
        await passwordReset.RequestAsync(
            new RequestAdminPasswordResetInputModel(administrator.Email, locale),
            cancellationToken);
        Audit(
            actorId,
            actorEmail,
            "administrator_password_reset_requested_by_administrator",
            administrator.Id,
            new { administrator.Email });
        await controlPlane.SaveChangesAsync(cancellationToken);
    }

    private void Audit(Guid actorId, string actorEmail, string action, Guid resourceId, object details) =>
        controlPlane.AdminAuditRecords.Add(new AdminAuditRecord
        {
            ActorAdminAccountId = actorId,
            ActorEmailSnapshot = actorEmail,
            Action = action,
            ResourceType = "admin_account",
            ResourceId = resourceId.ToString(),
            SafeDetailsJson = JsonSerializer.Serialize(details)
        });

    private static AdminAdministratorViewModel Map(AdminAccount value) => new(
        value.Id,
        value.Email ?? string.Empty,
        value.Name,
        value.IsActive,
        IdentityRoleNames.PlatformAdministrator,
        value.CreatedAt,
        value.UpdatedAt);

    private static void EnsureSucceeded(IdentityResult result, string fallback)
    {
        if (result.Succeeded) return;
        var message = string.Join(" ", result.Errors.Select(value => value.Description));
        throw new ArgumentException(string.IsNullOrWhiteSpace(message) ? fallback : message);
    }
}
