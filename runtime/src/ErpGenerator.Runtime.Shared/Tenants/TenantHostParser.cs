namespace ErpGenerator.Runtime.Shared.Tenants;

public sealed class TenantHostParser
{
    public string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var withoutPort = host.Split(':', 2)[0].Trim().ToLowerInvariant();
        return withoutPort;
    }

    public string? TryGetSubdomain(string? host, string rootDomain)
    {
        var normalized = NormalizeHost(host);
        var normalizedRoot = NormalizeHost(rootDomain);

        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return null;
        }

        if (normalized == normalizedRoot || normalized == $"www.{normalizedRoot}")
        {
            return null;
        }

        var suffix = $".{normalizedRoot}";
        if (!normalized.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var subdomain = normalized[..^suffix.Length].Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(subdomain) ? null : subdomain;
    }
}
