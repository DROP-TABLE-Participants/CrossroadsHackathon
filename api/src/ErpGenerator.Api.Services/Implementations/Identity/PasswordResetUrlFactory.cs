namespace ErpGenerator.Api.Services.Implementations.Identity;

internal static class PasswordResetUrlFactory
{
    public static string Create(string portalBaseUrl, string? locale, string path, string email, string token)
    {
        var supportedLocale = string.Equals(locale, "bg", StringComparison.OrdinalIgnoreCase) ? "bg" : "en";
        return $"{portalBaseUrl.TrimEnd('/')}/{supportedLocale}/{path}" +
               $"?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }
}
