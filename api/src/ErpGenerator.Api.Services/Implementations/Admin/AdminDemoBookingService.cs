using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Admin;

public sealed class AdminDemoBookingService(ErpGeneratorDbContext controlPlane) : IAdminDemoBookingService
{
    public async Task<AdminDemoBookingListViewModel> ListAsync(
        string? search,
        string? status,
        string? notificationStatus,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = AdminQueryRules.Page(page);
        pageSize = AdminQueryRules.PageSize(pageSize);
        var query = controlPlane.DemoBookings.AsNoTracking();
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(value =>
                EF.Functions.ILike(value.Email, $"%{term}%") ||
                EF.Functions.ILike(value.RepresentativeName, $"%{term}%") ||
                EF.Functions.ILike(value.CompanyName, $"%{term}%"));
        }
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(value => value.Status == status);
        if (!string.IsNullOrWhiteSpace(notificationStatus))
        {
            query = query.Where(value =>
                value.NotificationDeliveries.Any(delivery => delivery.Status == notificationStatus));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var bookings = await query.Include(value => value.NotificationDeliveries)
            .OrderByDescending(value => value.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new AdminDemoBookingListViewModel(
            bookings.Select(value => new AdminDemoBookingViewModel(
                value.Id,
                value.CustomerId,
                value.Email,
                value.RepresentativeName,
                value.CompanyName,
                value.StartDate,
                value.EndDate,
                value.Status,
                value.NotificationDeliveries
                    .OrderBy(item => item.CreatedAt)
                    .Select(AdminViewModelMapper.Notification)
                    .ToArray())).ToArray(),
            page,
            pageSize,
            totalCount);
    }

    public async Task<AdminNotificationDeliveryViewModel> ResendNotificationAsync(
        Guid actorId,
        string actorEmail,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        var delivery = await controlPlane.NotificationDeliveries.SingleOrDefaultAsync(
            value => value.Id == notificationId,
            cancellationToken)
            ?? throw new KeyNotFoundException("Notification delivery was not found.");
        if (delivery.Status == "processing")
            throw new InvalidOperationException("Notification delivery is already in progress.");
        delivery.Status = "pending";
        delivery.NextAttemptAt = null;
        delivery.LastError = null;
        controlPlane.AdminAuditRecords.Add(new AdminAuditRecord
        {
            ActorAdminAccountId = actorId,
            ActorEmailSnapshot = actorEmail,
            Action = "notification_resend_requested",
            ResourceType = "notification_delivery",
            ResourceId = notificationId.ToString(),
            SafeDetailsJson = JsonSerializer.Serialize(new { delivery.Kind })
        });
        await controlPlane.SaveChangesAsync(cancellationToken);
        return AdminViewModelMapper.Notification(delivery);
    }
}
