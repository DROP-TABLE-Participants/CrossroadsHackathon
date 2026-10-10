using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Admin;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Route("api/admin-auth")]
public sealed class AdminAuthenticationController(
    IAdminAuthenticationService administrators,
    IAdminPasswordResetService passwordReset) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<ActionResult<AdminAuthenticationViewModel>> Login(
        LoginAdminInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await administrators.LoginAsync(input, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("refresh")]
    public async Task<ActionResult<AdminAuthenticationViewModel>> Refresh(
        RefreshAdminSessionInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await administrators.RefreshAsync(input, cancellationToken));

    [Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
    [HttpGet("me")]
    public async Task<ActionResult<AdminViewModel>> Me(CancellationToken cancellationToken)
    {
        var admin = await administrators.GetCurrentAsync(User.RequireAdminId(), cancellationToken);
        return admin is null ? Unauthorized() : Ok(admin);
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        LogoutAdminSessionInputModel input,
        CancellationToken cancellationToken)
    {
        await administrators.LogoutAsync(input, cancellationToken);
        return NoContent();
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        RequestAdminPasswordResetInputModel input,
        CancellationToken cancellationToken)
    {
        await passwordReset.RequestAsync(input, cancellationToken);
        return Accepted();
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(
        ResetAdminPasswordInputModel input,
        CancellationToken cancellationToken)
    {
        await passwordReset.ResetAsync(input, cancellationToken);
        return NoContent();
    }
}
