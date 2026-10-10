namespace ErpGenerator.Runtime.Shared.Compilation;

public sealed class BundleCompilationException : Exception
{
    public BundleCompilationException(IReadOnlyList<string> errors)
        : base("ERP Bundle validation failed.")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
