namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminGenerationRunListViewModel(
    IReadOnlyList<AdminGenerationRunListItemViewModel> Runs,
    int Page,
    int PageSize,
    int TotalCount);
