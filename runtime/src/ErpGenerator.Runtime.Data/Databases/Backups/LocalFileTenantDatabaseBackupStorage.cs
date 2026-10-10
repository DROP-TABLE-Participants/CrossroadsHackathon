using Microsoft.Extensions.Options;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed class LocalFileTenantDatabaseBackupStorage(IOptions<TenantDatabaseBackupOptions> options)
    : ITenantDatabaseBackupStorage
{
    private readonly TenantDatabaseBackupOptions options = options.Value;

    public string Mode => TenantDatabaseBackupStorageMode.LocalFile.ToString();

    public async Task<TenantDatabaseBackupStorageUploadResult> UploadAsync(
        TenantDatabaseBackupStorageUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var objectKey = TenantDatabaseBackupStorageKeys.BuildObjectKey(
            string.Empty,
            request.Tenant,
            request.BackupId,
            request.CreatedAt);
        var destinationPath = ResolvePath(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? RootDirectory);

        await using (var source = File.OpenRead(request.SourceFilePath))
        await using (var destination = File.Create(destinationPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        var fileInfo = new FileInfo(destinationPath);
        return new TenantDatabaseBackupStorageUploadResult(
            Mode,
            objectKey,
            Bucket: null,
            LocalPath: destinationPath,
            ContentLength: fileInfo.Length,
            Sha256: await TenantDatabaseBackupChecksum.Sha256FileAsync(destinationPath, cancellationToken));
    }

    public async Task DownloadAsync(
        TenantDatabaseBackupStorageReference reference,
        string targetFilePath,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = ResolvePath(reference.ObjectKey);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Backup object was not found.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFilePath)) ?? RootDirectory);
        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(targetFilePath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    public Task<bool> ExistsAsync(
        TenantDatabaseBackupStorageReference reference,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(ResolvePath(reference.ObjectKey)));

    public async Task<TenantDatabaseBackupStorageObjectMetadata?> GetMetadataAsync(
        TenantDatabaseBackupStorageReference reference,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(reference.ObjectKey);
        if (!File.Exists(path))
        {
            return null;
        }

        var fileInfo = new FileInfo(path);
        return new TenantDatabaseBackupStorageObjectMetadata(
            fileInfo.Length,
            await TenantDatabaseBackupChecksum.Sha256FileAsync(path, cancellationToken));
    }

    private string ResolvePath(string objectKey)
    {
        var safeParts = objectKey
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(TenantDatabaseBackupStorageKeys.SanitizePart)
            .ToArray();
        return Path.Combine([RootDirectory, .. safeParts]);
    }

    private string RootDirectory => Path.GetFullPath(options.LocalDirectory);
}
