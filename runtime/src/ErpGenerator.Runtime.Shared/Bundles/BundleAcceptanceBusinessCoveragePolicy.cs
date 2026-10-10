namespace ErpGenerator.Runtime.Shared.Bundles;

/// <summary>
/// Shared business-coverage boundary used by both local generation and the
/// runtime deployment broker. Controller-added seed smoke is supplemental and
/// can never authorize staging readiness on its own.
/// </summary>
public static class BundleAcceptanceBusinessCoveragePolicy
{
    public const string MissingBusinessCoverageReason =
        "Generated acceptance tests contain only controller seed existence smoke; controller smoke cannot substitute for at least one typed executable business test.";

    public static bool HasBusinessCoverage(BundleAcceptanceTestDocument test) =>
        !test.Test.Origin.Equals(BundleAcceptanceTestOrigins.ControllerSmoke, StringComparison.Ordinal) &&
        (test.Steps.Count > 0 ||
         test.Assertions.Any(assertion =>
             !assertion.Type.Equals("record_exists", StringComparison.OrdinalIgnoreCase) ||
             !string.IsNullOrWhiteSpace(assertion.Where?.Field)));

    public static string? FindUnsupportedReason(
        IReadOnlyList<BundleAcceptanceTestDocument> tests,
        bool requireBusinessCoverage = true) =>
        requireBusinessCoverage && !tests.Any(HasBusinessCoverage)
            ? MissingBusinessCoverageReason
            : null;
}
