namespace ErpGenerator.Api.Shared.Generation;

public sealed record ConnectorResourceViewModel(
    string Id,
    string Title,
    string Kind,
    string? SourceUrl,
    DateTimeOffset? ModifiedAt,
    bool Selected,
    string? ParentId = null,
    int? SortIndex = null);
