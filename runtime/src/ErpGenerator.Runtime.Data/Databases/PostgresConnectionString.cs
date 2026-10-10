using Npgsql;

namespace ErpGenerator.Runtime.Data.Databases;

public static class PostgresConnectionString
{
    public static string Normalize(string connectionString, string? databaseOverride = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        var trimmed = connectionString.Trim();
        if (trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return FromUri(trimmed, databaseOverride);
        }

        var value = databaseOverride is not null
            ? trimmed.Replace("{database}", databaseOverride, StringComparison.OrdinalIgnoreCase)
            : trimmed;

        var builder = new NpgsqlConnectionStringBuilder(value);
        if (databaseOverride is not null && !trimmed.Contains("{database}", StringComparison.OrdinalIgnoreCase))
        {
            builder.Database = databaseOverride;
        }

        return builder.ConnectionString;
    }

    private static string FromUri(string value, string? databaseOverride)
    {
        var uri = new Uri(value);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Database = databaseOverride ?? uri.AbsolutePath.TrimStart('/'),
        };

        if (!uri.IsDefaultPort)
        {
            builder.Port = uri.Port;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
            {
                builder.Password = Uri.UnescapeDataString(parts[1]);
            }
        }

        return builder.ConnectionString;
    }
}
