namespace ErpGenerator.Api.Shared.Generation;

public sealed record GeneratedEntityDefinition(string Key, string Label, IReadOnlyList<GeneratedFieldDefinition> Fields);
