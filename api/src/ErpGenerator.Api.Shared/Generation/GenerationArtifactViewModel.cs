namespace ErpGenerator.Api.Shared.Generation;

public sealed record GenerationArtifactViewModel(Guid Id, string Kind, string Status, string? Summary, string Label);
