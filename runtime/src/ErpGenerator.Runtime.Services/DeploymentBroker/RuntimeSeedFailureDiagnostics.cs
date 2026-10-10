using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Services.DeploymentBroker;

internal sealed record RuntimeSeedFailureDiagnostic(
    string FailureCode,
    string SafeError,
    string? SeedRef,
    string? Entity,
    IReadOnlyList<string> Fields);

internal static class RuntimeSeedFailureDiagnostics
{
    internal const string FailureCode = "seed_validation_failed";
    internal const int MaximumSafeErrorLength = 480;
    private const int MaximumDisplayIdentifierLength = 56;
    private const int MaximumDisplayFields = 6;

    internal static bool TryCreate(Exception exception, out RuntimeSeedFailureDiagnostic diagnostic)
    {
        switch (exception)
        {
            case RuntimeSeedRecordValidationException recordValidation:
                var recordFields = BoundedFields(recordValidation.Fields);
                var recordMessage = $"Generated staging seed record '{BoundedIdentifier(recordValidation.SeedRef)}' " +
                                    $"for entity '{BoundedIdentifier(recordValidation.Entity)}' failed validation" +
                                    FieldSuffix(recordFields);
                diagnostic = new RuntimeSeedFailureDiagnostic(
                    FailureCode,
                    BoundedMessage(recordMessage),
                    recordValidation.SeedRef,
                    recordValidation.Entity,
                    recordFields);
                return true;

            case RuntimeSeedException:
                diagnostic = new RuntimeSeedFailureDiagnostic(
                    FailureCode,
                    "Generated staging seed data contains invalid record references or schema fields.",
                    null,
                    null,
                    []);
                return true;

            case RuntimeRecordValidationException recordValidation:
                var fields = BoundedFields(recordValidation.Errors.Select(error => error.Field));
                diagnostic = new RuntimeSeedFailureDiagnostic(
                    FailureCode,
                    BoundedMessage("Generated staging seed data failed record validation" + FieldSuffix(fields)),
                    null,
                    null,
                    fields);
                return true;

            default:
                diagnostic = null!;
                return false;
        }
    }

    private static IReadOnlyList<string> BoundedFields(IEnumerable<string> fields) =>
        fields
            .Select(BoundedIdentifier)
            .Where(field => field.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumDisplayFields)
            .ToArray();

    private static string FieldSuffix(IReadOnlyList<string> fields) =>
        fields.Count == 0
            ? "."
            : $" for fields: {string.Join(", ", fields)}.";

    private static string BoundedIdentifier(string? value)
    {
        var source = value?.Trim() ?? string.Empty;
        var safe = new string(source
            .Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':' ? character : '_')
            .ToArray());
        return safe.Length <= MaximumDisplayIdentifierLength
            ? safe
            : safe[..MaximumDisplayIdentifierLength];
    }

    private static string BoundedMessage(string message) =>
        message.Length <= MaximumSafeErrorLength
            ? message
            : message[..MaximumSafeErrorLength];
}
