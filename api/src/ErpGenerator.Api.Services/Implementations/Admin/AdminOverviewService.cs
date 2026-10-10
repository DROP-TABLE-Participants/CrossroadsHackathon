using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Admin;

public sealed class AdminOverviewService(
    ErpGeneratorDbContext controlPlane,
    AdminIdentityDbContext adminIdentity) : IAdminOverviewService
{
    private static readonly string[] NeedsAttentionStatuses =
        ["waiting_for_evidence", "needs_information"];

    public async Task<AdminOverviewViewModel> GetAsync(CancellationToken cancellationToken)
    {
        var activeCustomers = await controlPlane.CustomerAccounts.AsNoTracking()
            .CountAsync(value => value.IsActive, cancellationToken);
        var inactiveCustomers = await controlPlane.CustomerAccounts.AsNoTracking()
            .CountAsync(value => !value.IsActive, cancellationToken);
        var projectCount = await controlPlane.GenerationProjects.AsNoTracking()
            .CountAsync(cancellationToken);
        var needsAttention = await controlPlane.GenerationProjects.AsNoTracking()
            .CountAsync(value =>
                NeedsAttentionStatuses.Contains(value.Status) &&
                !value.GenerationRuns.Any(run =>
                    run.Status == GenerationRunStatuses.WaitingForInput &&
                    !run.PendingWorkItems.Any(item =>
                        item.Blocking &&
                        item.Status == "pending") &&
                    run.ResumeFailureCount <
                    GenerationRunPresentationStatus.ResumeFailureAttentionThreshold),
                cancellationToken);
        var failedProjects = await controlPlane.GenerationProjects.AsNoTracking()
            .CountAsync(value => value.Status == "failed", cancellationToken);
        var pendingNotifications = await controlPlane.NotificationDeliveries.AsNoTracking()
            .CountAsync(value => value.Status == "pending" || value.Status == "processing", cancellationToken);
        var failedNotifications = await controlPlane.NotificationDeliveries.AsNoTracking()
            .CountAsync(value => value.Status == "failed", cancellationToken);
        var activeAdministrators = await adminIdentity.AdminAccounts.AsNoTracking()
            .CountAsync(value => value.IsActive, cancellationToken);

        var recentProjects = await controlPlane.GenerationProjects.AsNoTracking()
            .OrderByDescending(value => value.UpdatedAt)
            .Take(6)
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
        var recentAudit = await controlPlane.AdminAuditRecords.AsNoTracking()
            .OrderByDescending(value => value.CreatedAt)
            .Take(8)
            .ToListAsync(cancellationToken);

        return new AdminOverviewViewModel(
            activeCustomers,
            inactiveCustomers,
            projectCount,
            needsAttention,
            failedProjects,
            pendingNotifications,
            failedNotifications,
            activeAdministrators,
            recentProjects,
            recentAudit.Select(AdminViewModelMapper.Audit).ToArray());
    }
}
