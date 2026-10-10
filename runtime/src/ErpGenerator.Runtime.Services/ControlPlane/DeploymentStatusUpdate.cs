namespace ErpGenerator.Runtime.Services.ControlPlane;

public sealed record DeploymentStatusUpdate(
    string Host,
    string Environment,
    string Status,
    string? Message = null,
    string? RuntimeUrl = null,
    string? ActivationUrl = null,
    string? BundleId = null,
    string? BundleVersion = null,
    string? BundleHash = null);

public sealed record DeploymentEventCreate(
    string Host,
    string Environment,
    string Type,
    string Message,
    string? Details = null);

public sealed record OwnerActivationStatusUpdate(
    string Host,
    string Environment,
    string Email,
    string Status,
    DateTimeOffset? ActivatedAt = null);
