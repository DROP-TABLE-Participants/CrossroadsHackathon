using System.Security.Cryptography;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

public static class TenantDatabaseBackupChecksum
{
    public static async Task<string> Sha256FileAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
