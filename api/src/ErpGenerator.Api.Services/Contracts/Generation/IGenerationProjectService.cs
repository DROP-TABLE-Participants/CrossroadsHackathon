namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface IGenerationProjectService
{
    Task<GenerationProjectListViewModel> ListAsync(Guid customerId, CancellationToken cancellationToken);
    Task<GenerationProjectViewModel?> GetAsync(Guid customerId, Guid projectId, CancellationToken cancellationToken);
    Task<GenerationProjectViewModel> CreateAsync(Guid customerId, CreateGenerationProjectInputModel input, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid customerId, Guid projectId, CancellationToken cancellationToken);
    Task<GenerationProjectViewModel> StartAsync(
        Guid customerId,
        Guid projectId,
        bool withAssumptions,
        StartGenerationInputModel? input,
        CancellationToken cancellationToken);
}
