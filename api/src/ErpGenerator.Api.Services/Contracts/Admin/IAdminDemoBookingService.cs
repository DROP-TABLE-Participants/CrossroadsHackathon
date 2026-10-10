namespace ErpGenerator.Api.Services.Contracts.Admin;

public interface IAdminDemoBookingService
{
    Task<AdminDemoBookingListViewModel> ListAsync(
        string? search,
        string? status,
        string? notificationStatus,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
    Task<AdminNotificationDeliveryViewModel> ResendNotificationAsync(
        Guid actorId,
        string actorEmail,
        Guid notificationId,
        CancellationToken cancellationToken);
}
