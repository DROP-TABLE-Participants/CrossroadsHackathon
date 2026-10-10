using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Demo;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
public sealed class DemoBookingsController(IDemoBookingService demoBookings) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("public-read")]
    [HttpGet("api/demo-booking/availability")]
    public async Task<ActionResult<DemoBookingAvailabilityViewModel>> Availability(CancellationToken cancellationToken) =>
        Ok(await demoBookings.AvailabilityAsync(cancellationToken));

    [Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
    [EnableRateLimiting("demo-booking")]
    [HttpPost("api/demo-bookings")]
    public async Task<ActionResult<DemoBookingViewModel>> Create(
        CreateDemoBookingInputModel input,
        CancellationToken cancellationToken)
    {
        var booking = await demoBookings.CreateAsync(User.RequireCustomerId(), input, cancellationToken);
        return Created($"/api/demo-bookings/{booking.Id}", booking);
    }
}
