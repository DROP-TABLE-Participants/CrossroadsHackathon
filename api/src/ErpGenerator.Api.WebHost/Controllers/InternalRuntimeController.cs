using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/internal/runtime")]
public sealed class InternalRuntimeController(IRuntimeControlPlaneService controlPlane) : ControllerBase
{
    [HttpGet("tenants/resolve")]
    public async Task<ActionResult<RuntimeTenantEnvironmentViewModel>> ResolveTenant(
        [FromQuery] string host,
        [FromQuery] string? environment,
        CancellationToken cancellationToken)
    {
        var result = await controlPlane.ResolveAsync(host, environment, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("tenants/schedule-targets")]
    public async Task<ActionResult<RuntimeScheduleTargetsViewModel>> ScheduleTargets(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await controlPlane.ListScheduleTargetsAsync(page, limit, cancellationToken));

    [HttpPost("tenant-db-secrets")]
    public async Task<IActionResult> StoreTenantDatabaseSecret(
        StoreTenantDatabaseCredentialInputModel input,
        CancellationToken cancellationToken)
    {
        await controlPlane.StoreCredentialAsync(input, cancellationToken);
        return NoContent();
    }

    [HttpGet("tenant-db-secrets/{secretReference}")]
    public async Task<ActionResult<TenantDatabaseCredentialViewModel>> ResolveTenantDatabaseSecret(
        string secretReference,
        CancellationToken cancellationToken)
    {
        var result = await controlPlane.ResolveCredentialAsync(secretReference, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("deployment-events")]
    public async Task<IActionResult> DeploymentEvent(RuntimeDeploymentEventInputModel input, CancellationToken cancellationToken)
    {
        await controlPlane.RecordDeploymentEventAsync(input, cancellationToken);
        return NoContent();
    }

    [HttpPost("deployments/status")]
    public async Task<IActionResult> DeploymentStatus(RuntimeDeploymentStatusInputModel input, CancellationToken cancellationToken)
    {
        await controlPlane.UpdateDeploymentStatusAsync(input, cancellationToken);
        return NoContent();
    }

    [HttpPost("owner-activations/status")]
    public async Task<IActionResult> OwnerActivationStatus(RuntimeOwnerActivationStatusInputModel input, CancellationToken cancellationToken)
    {
        await controlPlane.UpdateOwnerActivationStatusAsync(input, cancellationToken);
        return NoContent();
    }

    [HttpPost("feedback")]
    public async Task<IActionResult> Feedback(RuntimeFeedbackInputModel input, CancellationToken cancellationToken)
    {
        await controlPlane.SubmitFeedbackAsync(input, cancellationToken);
        return NoContent();
    }

    [HttpPost("usage-events")]
    public async Task<IActionResult> Usage(RuntimeUsageEventInputModel input, CancellationToken cancellationToken)
    {
        await controlPlane.SubmitUsageAsync(input, cancellationToken);
        return NoContent();
    }
}
