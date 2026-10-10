namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminOverviewViewModel(
    int ActiveCustomerCount,
    int InactiveCustomerCount,
    int GenerationProjectCount,
    int NeedsAttentionProjectCount,
    int FailedProjectCount,
    int PendingNotificationCount,
    int FailedNotificationCount,
    int ActiveAdministratorCount,
    IReadOnlyList<AdminGenerationProjectListItemViewModel> RecentProjects,
    IReadOnlyList<AdminAuditRecordViewModel> RecentAuditRecords);
