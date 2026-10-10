using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Admin;

public sealed class AdminGenerationProjectService(ErpGeneratorDbContext controlPlane)
    : IAdminGenerationProjectService
{
    public async Task<AdminGenerationProjectListViewModel> ListAsync(
        string? search,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = AdminQueryRules.Page(page);
        pageSize = AdminQueryRules.PageSize(pageSize);
        var query = controlPlane.GenerationProjects.AsNoTracking();
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(value =>
                EF.Functions.ILike(value.CompanyName, $"%{term}%") ||
                (value.Customer.Email != null && EF.Functions.ILike(value.Customer.Email, $"%{term}%")) ||
                EF.Functions.ILike(value.Customer.Name, $"%{term}%"));
        }
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(value => value.Status == status);

        var totalCount = await query.CountAsync(cancellationToken);
        var projects = await query.OrderByDescending(value => value.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
        return new AdminGenerationProjectListViewModel(projects, page, pageSize, totalCount);
    }

    public async Task<AdminGenerationProjectDiagnosticsViewModel?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await controlPlane.GenerationProjects.AsNoTracking().AsSplitQuery()
            .Include(value => value.Customer)
            .Include(value => value.Tenant).ThenInclude(value => value!.Environments)
            .Include(value => value.InputSources)
            .Include(value => value.InputDocuments)
            .Include(value => value.InterviewRequests)
            .Include(value => value.GenerationRuns).ThenInclude(value => value.PendingWorkItems)
            .Include(value => value.GenerationRuns).ThenInclude(value => value.EvidenceSnapshots)
            .Include(value => value.GenerationRuns).ThenInclude(value => value.BundleVersions)
                .ThenInclude(value => value.Deployments)
            .SingleOrDefaultAsync(value => value.Id == projectId, cancellationToken);
        if (project is null) return null;

        var run = project.GenerationRuns.OrderByDescending(value => value.CreatedAt).FirstOrDefault();
        var snapshot = run?.EvidenceSnapshots.OrderByDescending(value => value.Sequence).FirstOrDefault();
        var runtimeUrls = project.Tenant?.Environments.ToDictionary(
            value => value.Environment,
            value => value.RuntimeUrl,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string?>();
        var deployments = project.GenerationRuns
            .SelectMany(value => value.BundleVersions)
            .SelectMany(value => value.Deployments)
            .OrderByDescending(value => value.UpdatedAt)
            .Select(value => new AdminDeploymentViewModel(
                value.Id,
                value.Environment,
                value.Status,
                runtimeUrls.GetValueOrDefault(value.Environment),
                value.TestsStatus,
                value.AttemptCount,
                value.FailureCode,
                value.FailureMessage,
                value.UpdatedAt))
            .ToArray();
        var audit = await controlPlane.AdminAuditRecords.AsNoTracking()
            .Where(value =>
                value.ResourceType == "generation_project" &&
                value.ResourceId == projectId.ToString())
            .OrderByDescending(value => value.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        return new AdminGenerationProjectDiagnosticsViewModel(
            project.Id,
            project.CustomerId,
            project.CompanyName,
            project.Customer.Name,
            project.Customer.Email ?? string.Empty,
            project.Status,
            project.DiscoveryMode,
            project.Goal,
            project.Industry,
            project.CompanyType,
            project.InputSources.Count,
            project.InputDocuments.Count,
            project.InterviewRequests.Count,
            project.InterviewRequests.Count(value => value.Status == "answered" || value.Status == "completed"),
            run?.Id,
            run is null
                ? null
                : GenerationRunPresentationStatus.For(run),
            run?.ClaimCount ?? 0,
            run is not null &&
            GenerationRunPresentationStatus.For(run) ==
            GenerationRunPresentationStatus.ResumeFailed
                ? "generation_resume_failed"
                : run?.FailureCode,
            run is not null &&
            GenerationRunPresentationStatus.For(run) ==
            GenerationRunPresentationStatus.ResumeFailed
                ? run.ResumeFailureMessage
                : run?.FailureMessage,
            run?.HeartbeatAt,
            snapshot?.Sequence,
            snapshot?.ContentSha256,
            snapshot?.ItemCount ?? 0,
            run?.PendingWorkItems
                .Where(value => value.Status == "pending")
                .OrderBy(value => value.CreatedAt)
                .Select(value => new AdminPendingWorkViewModel(
                    value.Id,
                    value.Kind,
                    value.Status,
                    value.Blocking,
                    value.Prompt,
                    value.TargetRole,
                    value.CreatedAt)).ToArray() ?? [],
            project.InputSources.OrderBy(value => value.CreatedAt).Select(value => new AdminInputSourceViewModel(
                value.Id,
                value.ProviderConfigKey,
                value.SourceKind,
                value.Status,
                value.ScopeMode,
                value.LastRecordCount ?? 0,
                value.LastTextRecordCount ?? 0,
                value.LastError,
                value.LastWarning,
                value.LastCheckedAt,
                value.LastSyncedAt)).ToArray(),
            deployments,
            audit.Select(AdminViewModelMapper.Audit).ToArray());
    }
}
