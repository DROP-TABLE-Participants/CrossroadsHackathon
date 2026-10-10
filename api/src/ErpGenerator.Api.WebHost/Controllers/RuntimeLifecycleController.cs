using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
[Route("api/customers/generation-runs/{runId:guid}")]
public sealed class RuntimeLifecycleController(IRuntimeLifecycleService lifecycle) : ControllerBase
{
    [HttpPost("activation-link")]
    public async Task<ActionResult<object>> ActivationLink(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var activation = await lifecycle.CreateActivationLinkAsync(
            User.RequireCustomerId(), runId, cancellationToken);
        return Ok(new { activation });
    }
}
