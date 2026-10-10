using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed class S3CompatibleTenantDatabaseBackupStorage(
    IOptions<TenantDatabaseBackupOptions> options,
    ILogger<S3CompatibleTenantDatabaseBackupStorage>? logger = null)
    : ITenantDatabaseBackupStorage
{
    private readonly TenantDatabaseBackupS3Options options = options.Value.S3;

    public string Mode => TenantDatabaseBackupStorageMode.S3Compatible.ToString();

    public async Task<TenantDatabaseBackupStorageUploadResult> UploadAsync(
        TenantDatabaseBackupStorageUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var objectKey = TenantDatabaseBackupStorageKeys.BuildObjectKey(
            options.Prefix,
            request.Tenant,
            request.BackupId,
            request.CreatedAt);
        var fileInfo = new FileInfo(request.SourceFilePath);
        var sha256 = await TenantDatabaseBackupChecksum.Sha256FileAsync(request.SourceFilePath, cancellationToken);
        var putRequest = new PutObjectRequest
        {
            BucketName = Required(options.Bucket, "S3 backup bucket is not configured."),
            Key = objectKey,
            FilePath = request.SourceFilePath
        };
        putRequest.Metadata.Add("sha256", sha256);

        if (!string.IsNullOrWhiteSpace(options.ServerSideEncryption))
        {
            putRequest.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(options.ServerSideEncryption);
        }

        await client.PutObjectAsync(putRequest, cancellationToken);
        logger?.LogInformation(
            "Tenant database backup object uploaded to S3-compatible storage; object key {ObjectKey}; size {ContentLength} bytes.",
            objectKey,
            fileInfo.Length);

        return new TenantDatabaseBackupStorageUploadResult(
            Mode,
            objectKey,
            options.Bucket,
            LocalPath: null,
            fileInfo.Length,
            sha256);
    }

    public async Task DownloadAsync(
        TenantDatabaseBackupStorageReference reference,
        string targetFilePath,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var response = await client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = Required(reference.Bucket ?? options.Bucket, "S3 backup bucket is not configured."),
                Key = reference.ObjectKey
            },
            cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFilePath)) ?? Path.GetTempPath());
        await using var target = File.Create(targetFilePath);
        await response.ResponseStream.CopyToAsync(target, cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        TenantDatabaseBackupStorageReference reference,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetMetadataAsync(reference, cancellationToken) is not null;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    public async Task<TenantDatabaseBackupStorageObjectMetadata?> GetMetadataAsync(
        TenantDatabaseBackupStorageReference reference,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        try
        {
            var response = await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = Required(reference.Bucket ?? options.Bucket, "S3 backup bucket is not configured."),
                    Key = reference.ObjectKey
                },
                cancellationToken);
            return new TenantDatabaseBackupStorageObjectMetadata(
                response.Headers.ContentLength,
                TryGetMetadataValue(response, "sha256"));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    private AmazonS3Client CreateClient()
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle
        };
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            config.ServiceURL = options.Endpoint.Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region.Trim());
        }

        return new AmazonS3Client(
            new BasicAWSCredentials(
                Required(options.AccessKeyId, "S3 backup access key id is not configured."),
                Required(options.SecretAccessKey, "S3 backup secret access key is not configured.")),
            config);
    }

    private static string Required(string value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value.Trim();

    private static string? TryGetMetadataValue(GetObjectMetadataResponse response, string key)
    {
        foreach (var existingKey in response.Metadata.Keys)
        {
            if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return response.Metadata[existingKey];
            }
        }

        var prefixed = "x-amz-meta-" + key;
        foreach (var existingKey in response.Metadata.Keys)
        {
            if (string.Equals(existingKey, prefixed, StringComparison.OrdinalIgnoreCase))
            {
                return response.Metadata[existingKey];
            }
        }

        return null;
    }
}
