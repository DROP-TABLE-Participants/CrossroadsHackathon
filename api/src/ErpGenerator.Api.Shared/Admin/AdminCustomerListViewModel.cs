namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminCustomerListViewModel(
    IReadOnlyList<AdminCustomerViewModel> Customers,
    int Page,
    int PageSize,
    int TotalCount);
