namespace ErpGenerator.Runtime.Data.Databases.Backups;

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}
