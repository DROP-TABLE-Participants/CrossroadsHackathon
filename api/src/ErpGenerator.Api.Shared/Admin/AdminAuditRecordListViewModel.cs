namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminAuditRecordListViewModel(
    IReadOnlyList<AdminAuditRecordViewModel> Records,
    int Page,
    int PageSize,
    int TotalCount);
