namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminAdministratorListViewModel(
    IReadOnlyList<AdminAdministratorViewModel> Administrators,
    int Page,
    int PageSize,
    int TotalCount);
