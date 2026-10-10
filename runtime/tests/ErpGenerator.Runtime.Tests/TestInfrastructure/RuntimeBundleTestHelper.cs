using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

internal static class RuntimeBundleTestHelper
{
    public static string FindSampleBundlePath() => FindBundlePath("sample-erp.yaml");

    public static string FindBundlePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "bundles", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate bundles/{fileName}.");
    }

    public static string FindItServicesV4Folder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "bundles", "it-services", "v4");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("bundles/it-services/v4");
    }

    public static string FindManufacturingV1Folder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "bundles", "manufacturing", "v1");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("bundles/manufacturing/v1");
    }

    public static Task<ErpBundle> LoadSampleBundleAsync() => new ErpBundleYamlLoader().LoadFileAsync(FindSampleBundlePath());

    public static async Task<CompiledBundle> CompileSampleBundleAsync() =>
        new ErpBundleCompiler().Compile(await LoadSampleBundleAsync());

    public static async Task<CompiledBundle> CompileBundleFileAsync(string fileName) =>
        new ErpBundleCompiler().Compile(await new ErpBundleYamlLoader().LoadFileAsync(FindBundlePath(fileName)));

    public static async Task<CompiledBundle> CompileItServicesV4FolderAsync() =>
        new ErpBundleCompiler().Compile(await new ErpBundleYamlLoader().LoadDirectoryAsync(FindItServicesV4Folder()));

    public static async Task<CompiledBundle> CompileManufacturingV1FolderAsync() =>
        new ErpBundleCompiler().Compile(await new ErpBundleYamlLoader().LoadDirectoryAsync(FindManufacturingV1Folder()));

    public static string CopyItServicesV4FolderToTemp()
    {
        var source = FindItServicesV4Folder();
        var target = CreateTempDirectory();
        CopyDirectory(source, target);
        return target;
    }

    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"erpgenerator-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target, StringComparison.Ordinal));
        }
    }
}
