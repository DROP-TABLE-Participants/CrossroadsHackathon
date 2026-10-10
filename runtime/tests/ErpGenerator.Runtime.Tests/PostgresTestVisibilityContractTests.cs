using FluentAssertions;

namespace ErpGenerator.Runtime.Tests;

public sealed class PostgresTestVisibilityContractTests
{
    [Fact]
    public void Silent_postgres_detection_is_limited_to_the_shared_fixture_bootstrap()
    {
        var sourceDirectory = FindTestSourceDirectory();
        var testPostgresPath = Path.Combine(sourceDirectory, "TestInfrastructure", "TestPostgres.cs");
        const string methodName = "TryGet" + "AdminConnectionString(";
        var occurrences = Directory
            .EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, testPostgresPath, StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => new
            {
                Path = path,
                Line = line,
                LineNumber = index + 1
            }))
            .Where(entry => entry.Line.Contains(methodName, StringComparison.Ordinal))
            .ToArray();

        occurrences.Should().ContainSingle(
            "new silent PostgreSQL detection paths would make the default suite report misleading passes");
        Path.GetFileName(occurrences[0].Path).Should().Be("RuntimeActionPermissionEffectIntegrationTests.cs");
        occurrences[0].Line.Should().Contain("out var connectionString");
    }

    private static string FindTestSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "ErpGenerator.Runtime.Tests");
            if (File.Exists(Path.Combine(candidate, "ErpGenerator.Runtime.Tests.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the ErpGenerator.Runtime.Tests source directory.");
    }
}
