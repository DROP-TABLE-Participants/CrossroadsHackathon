using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ErpGenerator.Runtime.Services.Auth;

public sealed class RuntimeSessionService(IConfiguration configuration) : IRuntimeSessionService
{
    private readonly byte[] key = Encoding.UTF8.GetBytes(configuration["Runtime:SessionSecret"] ?? "local-runtime-session-secret-change-me");

    public string CreateSession(string tenantStableId, string host, long userId, string email, string role)
    {
        var payload = JsonSerializer.Serialize(new RuntimeSession(tenantStableId, host, userId, email, role, DateTimeOffset.UtcNow.AddHours(8)));
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var payloadText = Base64Url(payloadBytes);
        var signature = Sign(payloadText);
        return $"{payloadText}.{signature}";
    }

    public RuntimeSession? ValidateSession(string? token, string tenantStableId, string host)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !FixedEquals(parts[1], Sign(parts[0])))
        {
            return null;
        }

        var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
        var session = JsonSerializer.Deserialize<RuntimeSession>(payload);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return string.Equals(session.TenantStableId, tenantStableId, StringComparison.Ordinal) &&
               string.Equals(session.Host, host, StringComparison.OrdinalIgnoreCase)
            ? session
            : null;
    }

    private string Sign(string value)
    {
        using var hmac = new HMACSHA256(key);
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace("-", "+", StringComparison.Ordinal).Replace("_", "/", StringComparison.Ordinal);
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}
