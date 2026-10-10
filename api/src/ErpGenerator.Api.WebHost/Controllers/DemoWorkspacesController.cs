using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ErpGenerator.Api.Shared.Demo;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController, Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
[Route("api/customers")]
public sealed class DemoWorkspacesController(IDemoWorkspaceService demos) : ControllerBase
{
    [HttpGet("demo-catalog")] public IActionResult Catalog() => Ok(new { templates = new[] { demos.Catalog } });
    [HttpGet("demos")] public async Task<IActionResult> List(CancellationToken ct) => Ok(new { demos = await demos.ListAsync(User.RequireCustomerId(), ct) });
    [HttpPost("demos")] public async Task<IActionResult> Create(CancellationToken ct) => Ok(await demos.CreateAsync(User.RequireCustomerId(), null, ct));
    [HttpPost("demos/{id:guid}/reset")] public async Task<IActionResult> Reset(Guid id, CancellationToken ct) => Ok(await demos.CreateAsync(User.RequireCustomerId(), id, ct));
    [HttpPost("demos/{id:guid}/open")] public async Task<IActionResult> OpenDemo(Guid id, CancellationToken ct) => Ok(await demos.OpenAsync(User.RequireCustomerId(), id, null, null, ct));
    [HttpPost("generation-projects/create-from-template")] public async Task<IActionResult> Customize(CreateFromTemplateInputModel input, CancellationToken ct) => Ok(new GenerationProjectEnvelope(await demos.CustomizeAsync(User.RequireCustomerId(), input, ct)));
    [HttpPost("generation-runs/{runId:guid}/open")] public async Task<IActionResult> OpenGenerated(Guid runId, CancellationToken ct) => Ok(await demos.OpenAsync(User.RequireCustomerId(), null, null, runId, ct));
    [HttpPost("runtime/open")] public async Task<IActionResult> OpenHost([FromQuery] string host, CancellationToken ct) => Ok(await demos.OpenAsync(User.RequireCustomerId(), null, host, null, ct));
}
