using System.ComponentModel;
using System.Diagnostics;

namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed class ProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start process '{executable}'.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessRunResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (Win32Exception ex)
        {
            return new ProcessRunResult(127, string.Empty, ex.Message);
        }
    }
}
