using System.Security.Claims;

namespace ErpGenerator.Api.WebHost.Infrastructure;

public static class ClaimsPrincipalExtensions
{
    public static Guid RequireCustomerId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var customerId)
            ? customerId
            : throw new UnauthorizedAccessException("Customer identity is invalid.");
    }
}
