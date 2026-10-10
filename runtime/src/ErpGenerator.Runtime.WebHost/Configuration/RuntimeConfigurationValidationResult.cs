public sealed class RuntimeConfigurationValidationResult(IReadOnlyList<RuntimeConfigurationValidationError> errors)
{
    public IReadOnlyList<RuntimeConfigurationValidationError> Errors { get; } = errors;

    public bool IsValid => Errors.Count == 0;
}
