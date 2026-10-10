using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Admin;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
[EnableRateLimiting("admin")]
[Route("api/admin/generation-projects")]
public sealed class AdminGenerationProjectsController(IAdminGenerationProjectService projects) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminGenerationProjectListViewModel>> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await projects.ListAsync(search, status, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}/diagnostics")]
    public async Task<ActionResult<AdminGenerationProjectDiagnosticsViewModel>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(id, cancellationToken);
        return project is null ? NotFound(new { message = "Generation project was not found." }) : Ok(project);
    }
}
