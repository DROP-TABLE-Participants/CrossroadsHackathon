namespace ErpGenerator.Api.Services.Contracts.Admin;

public interface IAdminOrchestrationService
{
    Task<AdminOrchestrationOverviewViewModel> GetOverviewAsync(
        CancellationToken cancellationToken);

    Task<AdminGenerationRunListViewModel> ListAsync(
        string? search,
        string? status,
        string? stage,
        string? failureDisposition,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<AdminGenerationRunDiagnosticsViewModel?> GetAsync(
        Guid runId,
        CancellationToken cancellationToken);

    Task<AdminGenerationRunActionViewModel> RetryNowAsync(
        Guid actorId,
        string actorEmail,
        Guid runId,
        string reason,
        CancellationToken cancellationToken);

    Task<AdminGenerationRunActionViewModel> CancelAsync(
        Guid actorId,
        string actorEmail,
        Guid runId,
        string reason,
        CancellationToken cancellationToken);

    Task<AdminGenerationRunActionViewModel> ResumeAsync(
        Guid actorId,
        string actorEmail,
        Guid runId,
        string reason,
        CancellationToken cancellationToken);
}
