using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Admin;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
[EnableRateLimiting("admin")]
[Route("api/admin/audit")]
public sealed class AdminAuditController(IAdminAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminAuditRecordListViewModel>> List(
        [FromQuery] string? search,
        [FromQuery] string? action,
        [FromQuery] string? resourceType,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 30,
        CancellationToken cancellationToken = default) =>
        Ok(await audit.ListAsync(
            search,
            action,
            resourceType,
            from,
            to,
            page,
            pageSize,
            cancellationToken));
}
