using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ErpGenerator.Api.Shared.Tenants;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
[Route("api/customers")]
public sealed class CustomerFeedbackController(ICustomerFeedbackService feedback) : ControllerBase
{
    [HttpGet("erp-feedback")]
    public async Task<ActionResult<ErpFeedbackListViewModel>> Feedback(CancellationToken cancellationToken) =>
        Ok(await feedback.ListFeedbackAsync(User.RequireCustomerId(), cancellationToken));

    [HttpGet("erp-usage-observations")]
    public async Task<ActionResult<ErpUsageObservationListViewModel>> Usage(CancellationToken cancellationToken) =>
        Ok(await feedback.ListUsageAsync(User.RequireCustomerId(), cancellationToken));

    [HttpPost("erp-usage-observations/{observationId:guid}/dismiss")]
    public async Task<ActionResult<ErpUsageObservationEnvelope>> Dismiss(
        Guid observationId, CancellationToken cancellationToken) =>
        Ok(new ErpUsageObservationEnvelope(await feedback.DismissUsageAsync(
            User.RequireCustomerId(), observationId, cancellationToken)));

    [HttpGet("erp-change-proposals")]
    public ActionResult<ErpChangeProposalListViewModel> Proposals() => Ok(new ErpChangeProposalListViewModel([]));

    [HttpGet("erp-change-jobs")]
    public ActionResult<ErpChangeJobListViewModel> Jobs() => Ok(new ErpChangeJobListViewModel([]));
}
