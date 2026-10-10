using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Admin;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
[EnableRateLimiting("admin")]
[Route("api/admin/orchestration")]
public sealed class AdminOrchestrationController(
    IAdminOrchestrationService orchestration) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<AdminOrchestrationOverviewViewModel>> Overview(
        CancellationToken cancellationToken) =>
        Ok(await orchestration.GetOverviewAsync(cancellationToken));

    [HttpGet("runs")]
    public async Task<ActionResult<AdminGenerationRunListViewModel>> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? stage,
        [FromQuery] string? failureDisposition,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await orchestration.ListAsync(
            search,
            status,
            stage,
            failureDisposition,
            page,
            pageSize,
            cancellationToken));

    [HttpGet("runs/{id:guid}")]
    public async Task<ActionResult<AdminGenerationRunDiagnosticsViewModel>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var run = await orchestration.GetAsync(id, cancellationToken);
        return run is null
            ? NotFound(new { message = "Generation run was not found." })
            : Ok(run);
    }

    [HttpPost("runs/{id:guid}/retry")]
    public async Task<ActionResult<AdminGenerationRunActionViewModel>> Retry(
        Guid id,
        [FromBody] AdminGenerationRunControlInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await orchestration.RetryNowAsync(
            User.RequireAdminId(),
            User.RequireEmail(),
            id,
            input.Reason,
            cancellationToken));

    [HttpPost("runs/{id:guid}/cancel")]
    public async Task<ActionResult<AdminGenerationRunActionViewModel>> Cancel(
        Guid id,
        [FromBody] AdminGenerationRunControlInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await orchestration.CancelAsync(
            User.RequireAdminId(),
            User.RequireEmail(),
            id,
            input.Reason,
            cancellationToken));

    [HttpPost("runs/{id:guid}/resume")]
    public async Task<ActionResult<AdminGenerationRunActionViewModel>> Resume(
        Guid id,
        [FromBody] AdminGenerationRunControlInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await orchestration.ResumeAsync(
            User.RequireAdminId(),
            User.RequireEmail(),
            id,
            input.Reason,
            cancellationToken));
}
