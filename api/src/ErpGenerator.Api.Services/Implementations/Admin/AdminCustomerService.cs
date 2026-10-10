using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Admin;

public sealed class AdminCustomerService(
    ErpGeneratorDbContext controlPlane,
    ICustomerPasswordResetService passwordReset) : IAdminCustomerService
{
    public async Task<AdminCustomerListViewModel> ListAsync(
        string? search,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = AdminQueryRules.Page(page);
        pageSize = AdminQueryRules.PageSize(pageSize);
        var query = controlPlane.CustomerAccounts.AsNoTracking();
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(value =>
                (value.Email != null && EF.Functions.ILike(value.Email, $"%{term}%")) ||
                EF.Functions.ILike(value.Name, $"%{term}%") ||
                (value.CompanyName != null && EF.Functions.ILike(value.CompanyName, $"%{term}%")));
        }
        if (isActive is not null) query = query.Where(value => value.IsActive == isActive);

        var totalCount = await query.CountAsync(cancellationToken);
        var customers = await query
            .OrderByDescending(value => value.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(value => new AdminCustomerViewModel(
                value.Id,
                value.Email ?? string.Empty,
                value.Name,
                value.CompanyName,
                value.IsActive,
                value.CreatedAt,
                value.UpdatedAt,
                value.GenerationProjects.Count))
            .ToListAsync(cancellationToken);
        return new AdminCustomerListViewModel(customers, page, pageSize, totalCount);
    }

    public async Task<AdminCustomerDetailsViewModel?> GetAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var customer = await controlPlane.CustomerAccounts.AsNoTracking()
            .Where(value => value.Id == customerId)
            .Select(value => new AdminCustomerViewModel(
                value.Id,
                value.Email ?? string.Empty,
                value.Name,
                value.CompanyName,
                value.IsActive,
                value.CreatedAt,
                value.UpdatedAt,
                value.GenerationProjects.Count))
            .SingleOrDefaultAsync(cancellationToken);
        if (customer is null) return null;

        var projects = await controlPlane.GenerationProjects.AsNoTracking()
            .Where(value => value.CustomerId == customerId)
            .OrderByDescending(value => value.UpdatedAt)
            .Select(value => new AdminGenerationProjectListItemViewModel(
                value.Id,
                value.CustomerId,
                value.CompanyName,
                value.Customer.Name,
                value.Customer.Email ?? string.Empty,
                value.Status,
                value.DiscoveryMode,
                value.GenerationRuns.OrderByDescending(run => run.CreatedAt)
                    .Select(run =>
                        run.Status == GenerationRunStatuses.WaitingForInput &&
                        !run.PendingWorkItems.Any(item =>
                            item.Blocking &&
                            item.Status == "pending")
                            ? run.ResumeFailureCount >=
                              GenerationRunPresentationStatus.ResumeFailureAttentionThreshold
                                ? GenerationRunPresentationStatus.ResumeFailed
                                : GenerationRunPresentationStatus.Resuming
                            : run.Status)
                    .FirstOrDefault(),
                value.GenerationRuns.SelectMany(run => run.PendingWorkItems)
                    .Count(item => item.Status == "pending" && item.Blocking),
                value.UpdatedAt))
            .ToListAsync(cancellationToken);
        var audit = await controlPlane.AdminAuditRecords.AsNoTracking()
            .Where(value => value.ResourceType == "customer" && value.ResourceId == customerId.ToString())
            .OrderByDescending(value => value.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        return new AdminCustomerDetailsViewModel(
            customer,
            projects,
            audit.Select(AdminViewModelMapper.Audit).ToArray());
    }

    public async Task<AdminCustomerViewModel> SetStatusAsync(
        Guid actorId,
        string actorEmail,
        Guid customerId,
        UpdateAdminCustomerStatusInputModel input,
        CancellationToken cancellationToken)
    {
        var customer = await controlPlane.CustomerAccounts
            .Include(value => value.RefreshSessions)
            .Include(value => value.GenerationProjects)
            .SingleOrDefaultAsync(value => value.Id == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Customer account was not found.");
        customer.IsActive = input.IsActive;
        if (!input.IsActive)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var session in customer.RefreshSessions.Where(value => value.RevokedAt is null))
            {
                session.RevokedAt = now;
            }
        }
        Audit(actorId, actorEmail, "customer_status_changed", customerId, new { input.IsActive });
        await controlPlane.SaveChangesAsync(cancellationToken);
        return Map(customer);
    }

    public async Task RequestPasswordResetAsync(
        Guid actorId,
        string actorEmail,
        Guid customerId,
        string? locale,
        CancellationToken cancellationToken)
    {
        var customer = await controlPlane.CustomerAccounts.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Customer account was not found.");
        if (!customer.IsActive) throw new InvalidOperationException("Activate the customer before sending a password reset.");
        await passwordReset.RequestAsync(
            new RequestCustomerPasswordResetInputModel(customer.Email, locale),
            cancellationToken);
        Audit(actorId, actorEmail, "customer_password_reset_requested", customerId, new { customer.Email });
        await controlPlane.SaveChangesAsync(cancellationToken);
    }

    private void Audit(Guid actorId, string actorEmail, string action, Guid resourceId, object details) =>
        controlPlane.AdminAuditRecords.Add(new AdminAuditRecord
        {
            ActorAdminAccountId = actorId,
            ActorEmailSnapshot = actorEmail,
            Action = action,
            ResourceType = "customer",
            ResourceId = resourceId.ToString(),
            SafeDetailsJson = JsonSerializer.Serialize(details)
        });

    private static AdminCustomerViewModel Map(CustomerAccount value) => new(
        value.Id,
        value.Email ?? string.Empty,
        value.Name,
        value.CompanyName,
        value.IsActive,
        value.CreatedAt,
        value.UpdatedAt,
        value.GenerationProjects.Count);
}
