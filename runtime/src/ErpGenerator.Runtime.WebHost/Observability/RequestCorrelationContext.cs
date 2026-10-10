using Microsoft.AspNetCore.Http;

namespace ErpGenerator.Runtime.WebHost.Observability;

public static class RequestCorrelationContext
{
    public const string ItemKey = "__ErpGeneratorRequestId";

    public static string? GetRequestId(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(ItemKey, out var value) ? value as string : null;

    internal static void SetRequestId(HttpContext httpContext, string requestId) =>
        httpContext.Items[ItemKey] = requestId;
}
