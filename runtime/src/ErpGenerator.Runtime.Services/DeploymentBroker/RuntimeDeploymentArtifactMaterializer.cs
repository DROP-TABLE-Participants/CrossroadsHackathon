using System.Text;
using ErpGenerator.Runtime.Shared.Deployment;

namespace ErpGenerator.Runtime.Services.DeploymentBroker;

internal sealed class RuntimeDeploymentArtifactMaterializer : IAsyncDisposable
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".yaml",
        ".yml",
        ".json",
        ".md"
    };

    private RuntimeDeploymentArtifactMaterializer(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public static async Task<RuntimeDeploymentArtifactMaterializer> CreateAsync(
        RuntimeDeploymentArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Files.Count is < 1 or > RuntimeDeploymentBrokerLimits.MaximumFiles)
        {
            throw new RuntimeDeploymentBrokerValidationException("Bundle artifact contains an invalid number of files.");
        }

        if (!IsSha256(artifact.ArtifactHash) ||
            !string.Equals(
                RuntimeDeploymentArtifactHash.Compute(artifact.Files),
                artifact.ArtifactHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeDeploymentBrokerValidationException("Bundle artifact hash does not match its file manifest.");
        }

        var root = Path.Combine(Path.GetTempPath(), "erpgenerator-runtime-deployment-broker", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var materializer = new RuntimeDeploymentArtifactMaterializer(root);

        try
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var totalBytes = 0;
            foreach (var file in artifact.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                ValidateRelativePath(file.Path);
                if (!paths.Add(file.Path))
                {
                    throw new RuntimeDeploymentBrokerValidationException("Bundle artifact contains duplicate file paths.");
                }

                if (!IsSha256(file.Sha256) ||
                    !string.Equals(
                        RuntimeDeploymentArtifactHash.ComputeContent(file.Content),
                        file.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new RuntimeDeploymentBrokerValidationException($"Bundle artifact file '{file.Path}' failed hash validation.");
                }

                var byteCount = Encoding.UTF8.GetByteCount(file.Content);
                if (byteCount > RuntimeDeploymentBrokerLimits.MaximumFileBytes)
                {
                    throw new RuntimeDeploymentBrokerValidationException($"Bundle artifact file '{file.Path}' exceeds the per-file size limit.");
                }

                totalBytes = checked(totalBytes + byteCount);
                if (totalBytes > RuntimeDeploymentBrokerLimits.MaximumArtifactBytes)
                {
                    throw new RuntimeDeploymentBrokerValidationException("Bundle artifact exceeds the total size limit.");
                }

                var fullPath = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                var expectedPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
                {
                    throw new RuntimeDeploymentBrokerValidationException("Bundle artifact contains an unsafe file path.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, file.Content, new UTF8Encoding(false), cancellationToken);
            }

            return materializer;
        }
        catch
        {
            await materializer.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch
        {
            // The request-scoped artifact contains no credentials and the pod /tmp volume is ephemeral.
        }

        return ValueTask.CompletedTask;
    }

    private static void ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 512 ||
            Path.IsPathRooted(value) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Split('/').Any(segment => segment is "" or "." or "..") ||
            !AllowedExtensions.Contains(Path.GetExtension(value)))
        {
            throw new RuntimeDeploymentBrokerValidationException("Bundle artifact contains an unsupported or unsafe file path.");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

internal sealed class RuntimeDeploymentBrokerValidationException(string message) : Exception(message);
