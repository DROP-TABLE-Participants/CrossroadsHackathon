namespace ErpGenerator.Api.Services.Contracts.Demo;

public interface IDemoWorkspaceService
{
    DemoTemplateViewModel Catalog { get; }
    Task<IReadOnlyList<DemoInstanceViewModel>> ListAsync(Guid customerId, CancellationToken ct);
    Task<DemoInstanceViewModel> CreateAsync(Guid customerId, Guid? replacesId, CancellationToken ct);
    Task<GenerationProjectViewModel> CustomizeAsync(Guid customerId, CreateFromTemplateInputModel input, CancellationToken ct);
    Task<RuntimeGuestEntryViewModel> OpenAsync(Guid customerId, Guid? demoId, string? host, Guid? runId, string environment, CancellationToken ct);
    Task PrepareAsync(int count, CancellationToken ct);
    Task<bool> ProcessNextAsync(CancellationToken ct);
}
