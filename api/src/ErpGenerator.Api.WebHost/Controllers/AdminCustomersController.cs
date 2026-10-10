using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Admin;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
[EnableRateLimiting("admin")]
[Route("api/admin/customers")]
public sealed class AdminCustomersController(IAdminCustomerService customers) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminCustomerListViewModel>> List(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await customers.ListAsync(search, isActive, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AdminCustomerDetailsViewModel>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var customer = await customers.GetAsync(id, cancellationToken);
        return customer is null ? NotFound(new { message = "Customer account was not found." }) : Ok(customer);
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<AdminCustomerViewModel>> SetStatus(
        Guid id,
        UpdateAdminCustomerStatusInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await customers.SetStatusAsync(
            User.RequireAdminId(),
            User.RequireEmail(),
            id,
            input,
            cancellationToken));

    [HttpPost("{id:guid}/password-reset")]
    public async Task<IActionResult> RequestPasswordReset(
        Guid id,
        RequestAdminManagedPasswordResetInputModel input,
        CancellationToken cancellationToken)
    {
        await customers.RequestPasswordResetAsync(
            User.RequireAdminId(),
            User.RequireEmail(),
            id,
            input.Locale,
            cancellationToken);
        return Accepted(new { message = "Password-reset request accepted." });
    }
}
