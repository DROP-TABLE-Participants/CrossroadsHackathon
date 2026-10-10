namespace ErpGenerator.Api.Shared.Generation;

public sealed record GeneratedFieldDefinition(string Key, string Label, string Type, bool Required, string? References);
