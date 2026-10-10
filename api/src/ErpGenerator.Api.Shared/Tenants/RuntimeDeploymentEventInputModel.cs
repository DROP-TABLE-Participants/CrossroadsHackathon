using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeDeploymentEventInputModel(
    string Host,
    string Environment,
    string Type,
    string Message,
    string? Details = null);
