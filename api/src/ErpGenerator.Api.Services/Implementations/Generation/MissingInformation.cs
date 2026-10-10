namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed record MissingInformation(
    string Key,
    string Kind,
    string Question,
    string Reason,
    string TargetRole);
