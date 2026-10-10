public sealed class RuntimeConfigurationException : InvalidOperationException
{
    public RuntimeConfigurationException(RuntimeConfigurationValidationResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    public RuntimeConfigurationValidationResult Result { get; }

    private static string BuildMessage(RuntimeConfigurationValidationResult result)
    {
        var lines = result.Errors.Select(error => $"- {error.Key}: {error.Message}");
        return "Runtime configuration is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
