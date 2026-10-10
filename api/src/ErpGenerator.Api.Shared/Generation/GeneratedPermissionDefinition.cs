namespace ErpGenerator.Api.Shared.Generation;

public sealed record GeneratedPermissionDefinition(string Role, IReadOnlyList<string> Grants);
