namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed record DiscoveryAssessment(
    bool Sufficient,
    string Summary,
    IReadOnlyList<MissingInformation> MissingInformation);
