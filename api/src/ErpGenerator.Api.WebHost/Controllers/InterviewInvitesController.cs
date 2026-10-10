using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
[EnableRateLimiting("invites")]
[Route("api/customers/interview-requests")]
public sealed class InterviewInvitesController(IGenerationWorkflowService workflow) : ControllerBase
{
    [HttpPost("{id:guid}/copy-link")]
    public async Task<ActionResult<InterviewInviteViewModel>> CopyLink(Guid id, CancellationToken cancellationToken) =>
        Ok(await workflow.RotateInviteAsync(User.RequireCustomerId(), id, cancellationToken));

    [HttpPost("{id:guid}/send-invite")]
    public async Task<ActionResult<InterviewInviteViewModel>> SendInvite(Guid id, CancellationToken cancellationToken) =>
        Ok(await workflow.SendInviteAsync(User.RequireCustomerId(), id, cancellationToken));
}
