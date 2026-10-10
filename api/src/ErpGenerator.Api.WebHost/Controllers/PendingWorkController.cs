using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
[Route("api/customers/pending-work-items")]
public sealed class PendingWorkController(IGenerationWorkflowService workflow) : ControllerBase
{
    [HttpPost("{id:guid}/add-evidence-document")]
    public async Task<ActionResult<PendingWorkResolutionViewModel>> AddEvidence(
        Guid id,
        AddEvidenceDocumentInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await workflow.AddEvidenceAsync(User.RequireCustomerId(), id, input, cancellationToken));

    [HttpPost("{id:guid}/resolve")]
    public async Task<ActionResult<PendingWorkResolutionViewModel>> Resolve(
        Guid id,
        ResolvePendingWorkInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await workflow.ResolveAsync(User.RequireCustomerId(), id, input, cancellationToken));

    [HttpPost("{id:guid}/create-interview-request")]
    public async Task<ActionResult<InterviewInviteViewModel>> CreateInterview(
        Guid id,
        CreateInterviewRequestInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await workflow.CreateInterviewAsync(User.RequireCustomerId(), id, input, cancellationToken));
}
