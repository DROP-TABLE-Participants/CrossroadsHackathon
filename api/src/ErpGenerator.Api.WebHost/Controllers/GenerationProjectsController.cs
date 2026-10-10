using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
[Route("api/customers/generation-projects")]
public sealed class GenerationProjectsController(IGenerationProjectService projects) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GenerationProjectListViewModel>> List(CancellationToken cancellationToken) =>
        Ok(await projects.ListAsync(User.RequireCustomerId(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GenerationProjectEnvelope>> Get(Guid id, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(User.RequireCustomerId(), id, cancellationToken);
        return project is null ? NotFound(new { message = "Generation project was not found." }) : Ok(new GenerationProjectEnvelope(project));
    }

    [HttpPost]
    public async Task<ActionResult<GenerationProjectEnvelope>> Create(
        CreateGenerationProjectInputModel input,
        CancellationToken cancellationToken)
    {
        var project = await projects.CreateAsync(User.RequireCustomerId(), input, cancellationToken);
        return Created($"/api/customers/generation-projects/{project.Id}", new GenerationProjectEnvelope(project));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await projects.DeleteAsync(User.RequireCustomerId(), id, cancellationToken)
            ? Ok(new { ok = true, deletedProjectId = id })
            : NotFound(new { message = "Generation project was not found." });

    [HttpPost("{id:guid}/start-generation")]
    [EnableRateLimiting("generation-start")]
    public async Task<ActionResult<StartGenerationViewModel>> Start(
        Guid id,
        StartGenerationInputModel input,
        CancellationToken cancellationToken) =>
        Ok(new StartGenerationViewModel(
            true,
            await projects.StartAsync(User.RequireCustomerId(), id, false, input, cancellationToken)));

    [HttpPost("{id:guid}/generate-with-assumptions")]
    [EnableRateLimiting("generation-start")]
    public async Task<ActionResult<StartGenerationViewModel>> GenerateWithAssumptions(Guid id, CancellationToken cancellationToken) =>
        Ok(new StartGenerationViewModel(
            true,
            await projects.StartAsync(User.RequireCustomerId(), id, true, null, cancellationToken)));
}
