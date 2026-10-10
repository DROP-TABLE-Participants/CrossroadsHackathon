using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.WebHost.Infrastructure;

public sealed class InternalApiAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<InternalApiOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/internal", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var expected = options.Value.Secret;
        var supplied = context.Request.Headers["x-erpgenerator-internal-secret"].ToString();
        if (string.IsNullOrWhiteSpace(expected) || !FixedTimeEquals(expected, supplied))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }
}
