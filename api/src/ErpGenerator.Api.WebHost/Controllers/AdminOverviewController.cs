using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Admin;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
[EnableRateLimiting("admin")]
[Route("api/admin/overview")]
public sealed class AdminOverviewController(IAdminOverviewService overview) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminOverviewViewModel>> Get(CancellationToken cancellationToken) =>
        Ok(await overview.GetAsync(cancellationToken));
}
