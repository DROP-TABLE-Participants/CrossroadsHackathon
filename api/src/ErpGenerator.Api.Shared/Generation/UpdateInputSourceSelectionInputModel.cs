namespace ErpGenerator.Api.Shared.Generation;

public sealed record UpdateInputSourceSelectionInputModel(
    string? Mode,
    IReadOnlyList<string>? IncludedRootIds,
    IReadOnlyList<string>? ExcludedIds,
    bool IncludeDescendants = true,
    bool IncludeNewContent = true);
