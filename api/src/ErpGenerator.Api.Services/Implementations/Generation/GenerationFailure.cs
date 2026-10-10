namespace ErpGenerator.Api.Services.Implementations.Generation;

internal sealed record GenerationFailure(
    string Disposition,
    string Code,
    string CustomerMessage,
    string AdminMessage);
