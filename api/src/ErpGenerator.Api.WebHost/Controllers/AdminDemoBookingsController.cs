using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Admin;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
[EnableRateLimiting("admin")]
[Route("api/admin")]
public sealed class AdminDemoBookingsController(IAdminDemoBookingService bookings) : ControllerBase
{
    [HttpGet("demo-bookings")]
    public async Task<ActionResult<AdminDemoBookingListViewModel>> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? notificationStatus,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await bookings.ListAsync(
            search,
            status,
            notificationStatus,
            page,
            pageSize,
            cancellationToken));

    [HttpPost("notification-deliveries/{id:guid}/resend")]
    public async Task<ActionResult<AdminNotificationDeliveryViewModel>> Resend(
        Guid id,
        CancellationToken cancellationToken) =>
        Ok(await bookings.ResendNotificationAsync(
            User.RequireAdminId(),
            User.RequireEmail(),
            id,
            cancellationToken));
}
