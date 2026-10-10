using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("nango")]
public sealed class NangoWebhookController(INangoInputSourceService sources) : ControllerBase
{
    [HttpPost("api/integrations/nango/webhook")]
    public async Task<IActionResult> Handle(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        await sources.HandleWebhookAsync(await reader.ReadToEndAsync(cancellationToken), cancellationToken);
        return NoContent();
    }
}
