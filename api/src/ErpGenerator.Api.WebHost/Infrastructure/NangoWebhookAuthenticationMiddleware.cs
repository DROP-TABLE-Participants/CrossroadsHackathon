using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Services.Options.Generation;

namespace ErpGenerator.Api.WebHost.Infrastructure;

public sealed class NangoWebhookAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<NangoOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.Equals("/api/integrations/nango/webhook", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        var key = options.Value.WebhookSigningKey;
        var supplied = context.Request.Headers["X-Nango-Hmac-Sha256"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(supplied))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body));
        byte[] actual;
        try
        {
            actual = supplied.Length == expected.Length * 2
                ? Convert.FromHexString(supplied)
                : Convert.FromBase64String(supplied);
        }
        catch (FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(context);
    }
}
