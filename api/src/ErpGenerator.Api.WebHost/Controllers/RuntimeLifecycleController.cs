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
    [HttpPost("promote-production")]
    public async Task<ActionResult<GenerationRunActionViewModel>> PromoteProduction(
        Guid runId,
        PromoteProductionInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await lifecycle.RequestProductionPromotionAsync(
            User.RequireCustomerId(), runId, input.Confirm, cancellationToken));

    [HttpPost("environments/{environment}/activation-link")]
    public async Task<ActionResult<object>> ActivationLink(
        Guid runId,
        string environment,
        CancellationToken cancellationToken)
    {
        var activation = await lifecycle.CreateActivationLinkAsync(
            User.RequireCustomerId(), runId, environment, cancellationToken);
        return Ok(new { activation });
    }
}
