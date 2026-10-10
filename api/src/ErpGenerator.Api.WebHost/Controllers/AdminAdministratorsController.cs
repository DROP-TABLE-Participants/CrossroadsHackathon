using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Admin;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
[EnableRateLimiting("admin")]
[Route("api/admin/administrators")]
public sealed class AdminAdministratorsController(IAdminAccountManagementService administrators) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminAdministratorListViewModel>> List(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await administrators.ListAsync(search, isActive, page, pageSize, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<AdminAdministratorViewModel>> Create(
        CreateAdminAdministratorInputModel input,
        CancellationToken cancellationToken)
    {
        var administrator = await administrators.CreateAsync(
            User.RequireAdminId(),
            User.RequireEmail(),
            input,
            cancellationToken);
        return Created($"/api/admin/administrators/{administrator.Id}", administrator);
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<AdminAdministratorViewModel>> SetStatus(
        Guid id,
        UpdateAdminAdministratorStatusInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await administrators.SetStatusAsync(
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
        await administrators.RequestPasswordResetAsync(
            User.RequireAdminId(),
            User.RequireEmail(),
            id,
            input.Locale,
            cancellationToken);
        return Accepted(new { message = "Password-reset request accepted." });
    }
}
