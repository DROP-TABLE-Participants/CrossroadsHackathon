using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace ErpGenerator.Runtime.Tests;

internal static class RuntimeHttpTestHelper
{
    public static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string uri, string host, string cookie)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Host = host;
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    public static string CreateSessionCookie(
        string sessionSecret,
        string tenantStableId,
        string host,
        long userId,
        string email,
        string role)
    {
        var sessionService = new RuntimeSessionService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Runtime:SessionSecret"] = sessionSecret })
            .Build());
        return $"erpgenerator_runtime_session={sessionService.CreateSession(tenantStableId, host, userId, email, role)}";
    }
}
