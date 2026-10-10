namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminCustomerDetailsViewModel(
    AdminCustomerViewModel Customer,
    IReadOnlyList<AdminGenerationProjectListItemViewModel> Projects,
    IReadOnlyList<AdminAuditRecordViewModel> RecentAuditRecords);
