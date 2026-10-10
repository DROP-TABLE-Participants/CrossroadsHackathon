using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Identity;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
[Route("api/customers")]
public sealed class CustomersController(
    ICustomerService customers,
    ICustomerPasswordResetService passwordReset) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("guest-entry")]
    [HttpPost("guest-session")]
    public async Task<ActionResult<CustomerAuthenticationViewModel>> Guest(CancellationToken cancellationToken) =>
        Ok(await customers.CreateGuestAsync(cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    public async Task<ActionResult<CustomerAuthenticationViewModel>> Register(
        RegisterCustomerInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await customers.RegisterAsync(input, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<ActionResult<CustomerAuthenticationViewModel>> Login(
        LoginCustomerInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await customers.LoginAsync(input, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("refresh")]
    public async Task<ActionResult<CustomerAuthenticationViewModel>> Refresh(
        RefreshCustomerSessionInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await customers.RefreshAsync(input, cancellationToken));

    [HttpGet("me")]
    public async Task<ActionResult<CurrentCustomerViewModel>> Me(CancellationToken cancellationToken)
    {
        var customer = await customers.GetCurrentAsync(User.RequireCustomerId(), cancellationToken);
        return customer is null ? Unauthorized() : Ok(customer);
    }

    [HttpPatch("me")]
    public async Task<ActionResult<CurrentCustomerViewModel>> Update(
        UpdateCustomerInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await customers.UpdateAsync(User.RequireCustomerId(), input, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        LogoutCustomerSessionInputModel input,
        CancellationToken cancellationToken)
    {
        await customers.LogoutAsync(input, cancellationToken);
        return NoContent();
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        RequestCustomerPasswordResetInputModel input,
        CancellationToken cancellationToken)
    {
        await passwordReset.RequestAsync(input, cancellationToken);
        return Accepted();
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(
        ResetCustomerPasswordInputModel input,
        CancellationToken cancellationToken)
    {
        await passwordReset.ResetAsync(input, cancellationToken);
        return NoContent();
    }
}
