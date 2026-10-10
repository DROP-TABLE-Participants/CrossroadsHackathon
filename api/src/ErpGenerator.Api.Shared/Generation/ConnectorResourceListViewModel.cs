namespace ErpGenerator.Api.Shared.Generation;

public sealed record ConnectorResourceListViewModel(
    GenerationInputSourceViewModel Source,
    IReadOnlyList<ConnectorResourceViewModel> Resources,
    string? NextCursor);
