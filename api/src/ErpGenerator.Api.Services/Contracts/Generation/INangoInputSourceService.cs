namespace ErpGenerator.Api.Services.Contracts.Generation;

public interface INangoInputSourceService
{
    IReadOnlyList<string> AvailableConnectorKeys { get; }
    Task<NangoConnectSessionViewModel> CreateConnectSessionAsync(
        Guid customerId, Guid projectId, CreateNangoConnectSessionInputModel input, CancellationToken cancellationToken);
    Task<ReconcileInputSourcesViewModel> ReconcileAsync(
        Guid customerId, Guid projectId, CancellationToken cancellationToken);
    Task<ConnectorResourceListViewModel> ListResourcesAsync(
        Guid customerId,
        Guid projectId,
        Guid sourceId,
        string? cursor,
        string? query,
        CancellationToken cancellationToken);
    Task<GenerationInputSourceViewModel> UpdateSelectionAsync(
        Guid customerId,
        Guid projectId,
        Guid sourceId,
        UpdateInputSourceSelectionInputModel input,
        CancellationToken cancellationToken);
    Task<GenerationInputSourceViewModel> RefreshAsync(
        Guid customerId, Guid projectId, Guid sourceId, CancellationToken cancellationToken);
    Task DisconnectAsync(
        Guid customerId, Guid projectId, Guid sourceId, CancellationToken cancellationToken);
    Task DisconnectProjectSourcesAsync(
        Guid customerId, Guid projectId, CancellationToken cancellationToken);
    Task<int> ImportConnectedEvidenceAsync(
        Guid customerId, Guid projectId, CancellationToken cancellationToken);
    Task HandleWebhookAsync(string body, CancellationToken cancellationToken);
    Task<int> PurgeExpiredContentAsync(CancellationToken cancellationToken);
}
