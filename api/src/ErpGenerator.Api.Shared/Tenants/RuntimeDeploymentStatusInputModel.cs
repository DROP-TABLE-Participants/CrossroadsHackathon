using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeDeploymentStatusInputModel(
    string Host,
    string Environment,
    string Status,
    string? Message = null,
    string? RuntimeUrl = null,
    string? ActivationUrl = null,
    string? BundleId = null,
    string? BundleVersion = null,
    string? BundleHash = null);
