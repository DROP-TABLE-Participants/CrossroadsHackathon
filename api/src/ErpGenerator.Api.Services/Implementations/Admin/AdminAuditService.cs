using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Admin;

public sealed class AdminAuditService(ErpGeneratorDbContext controlPlane) : IAdminAuditService
{
    public async Task<AdminAuditRecordListViewModel> ListAsync(
        string? search,
        string? action,
        string? resourceType,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = AdminQueryRules.Page(page);
        pageSize = AdminQueryRules.PageSize(pageSize);
        var query = controlPlane.AdminAuditRecords.AsNoTracking();
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(value =>
                EF.Functions.ILike(value.ActorEmailSnapshot, $"%{term}%") ||
                EF.Functions.ILike(value.ResourceId, $"%{term}%"));
        }
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(value => value.Action == action);
        if (!string.IsNullOrWhiteSpace(resourceType))
            query = query.Where(value => value.ResourceType == resourceType);
        if (from is not null) query = query.Where(value => value.CreatedAt >= from);
        if (to is not null) query = query.Where(value => value.CreatedAt <= to);

        var totalCount = await query.CountAsync(cancellationToken);
        var records = await query.OrderByDescending(value => value.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new AdminAuditRecordListViewModel(
            records.Select(AdminViewModelMapper.Audit).ToArray(),
            page,
            pageSize,
            totalCount);
    }
}
