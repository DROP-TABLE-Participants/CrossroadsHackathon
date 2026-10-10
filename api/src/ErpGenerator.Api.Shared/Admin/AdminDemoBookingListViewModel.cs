namespace ErpGenerator.Api.Shared.Admin;

public sealed record AdminDemoBookingListViewModel(
    IReadOnlyList<AdminDemoBookingViewModel> Bookings,
    int Page,
    int PageSize,
    int TotalCount);
