namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminDemoBookingViewModel(
    Guid Id,
    Guid CustomerId,
    string Email,
    string RepresentativeName,
    string CompanyName,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    string Status,
    IReadOnlyList<AdminNotificationDeliveryViewModel> Notifications);
