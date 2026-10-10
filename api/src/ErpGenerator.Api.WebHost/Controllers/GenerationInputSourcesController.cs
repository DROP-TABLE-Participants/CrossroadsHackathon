using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
[EnableRateLimiting("nango")]
[Route("api/customers/generation-projects/{projectId:guid}")]
public sealed class GenerationInputSourcesController(INangoInputSourceService sources) : ControllerBase
{
    [HttpPost("nango/connect-session")]
    public async Task<ActionResult<NangoConnectSessionViewModel>> Connect(
        Guid projectId, CreateNangoConnectSessionInputModel input, CancellationToken cancellationToken) =>
        Ok(await sources.CreateConnectSessionAsync(User.RequireCustomerId(), projectId, input, cancellationToken));

    [HttpPost("input-sources/reconcile")]
    public async Task<ActionResult<ReconcileInputSourcesViewModel>> Reconcile(
        Guid projectId, CancellationToken cancellationToken) =>
        Ok(await sources.ReconcileAsync(User.RequireCustomerId(), projectId, cancellationToken));

    [HttpGet("input-sources/{sourceId:guid}/resources")]
    public async Task<ActionResult<ConnectorResourceListViewModel>> Resources(
        Guid projectId,
        Guid sourceId,
        [FromQuery] string? cursor,
        [FromQuery] string? query,
        CancellationToken cancellationToken) =>
        Ok(await sources.ListResourcesAsync(
            User.RequireCustomerId(), projectId, sourceId, cursor, query, cancellationToken));

    [HttpPut("input-sources/{sourceId:guid}/selection")]
    public async Task<ActionResult<GenerationInputSourceViewModel>> Select(
        Guid projectId,
        Guid sourceId,
        UpdateInputSourceSelectionInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await sources.UpdateSelectionAsync(
            User.RequireCustomerId(), projectId, sourceId, input, cancellationToken));

    [HttpPost("input-sources/{sourceId:guid}/refresh")]
    public async Task<ActionResult<GenerationInputSourceViewModel>> Refresh(
        Guid projectId, Guid sourceId, CancellationToken cancellationToken) =>
        Ok(await sources.RefreshAsync(
            User.RequireCustomerId(), projectId, sourceId, cancellationToken));

    [HttpDelete("input-sources/{sourceId:guid}")]
    public async Task<IActionResult> Disconnect(
        Guid projectId, Guid sourceId, CancellationToken cancellationToken)
    {
        await sources.DisconnectAsync(
            User.RequireCustomerId(), projectId, sourceId, cancellationToken);
        return NoContent();
    }
}
