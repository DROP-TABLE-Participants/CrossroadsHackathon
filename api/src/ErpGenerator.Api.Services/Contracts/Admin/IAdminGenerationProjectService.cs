namespace ErpGenerator.Api.Services.Contracts.Admin;

public interface IAdminGenerationProjectService
{
    Task<AdminGenerationProjectListViewModel> ListAsync(
        string? search,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
    Task<AdminGenerationProjectDiagnosticsViewModel?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken);
}
