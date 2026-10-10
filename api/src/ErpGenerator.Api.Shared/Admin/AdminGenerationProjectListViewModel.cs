namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminGenerationProjectListViewModel(
    IReadOnlyList<AdminGenerationProjectListItemViewModel> Projects,
    int Page,
    int PageSize,
    int TotalCount);
