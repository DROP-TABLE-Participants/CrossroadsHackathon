namespace ErpGenerator.Api.Services.Contracts.Admin;

public interface IAdminAuditService
{
    Task<AdminAuditRecordListViewModel> ListAsync(
        string? search,
        string? action,
        string? resourceType,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
