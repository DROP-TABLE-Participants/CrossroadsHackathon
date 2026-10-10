using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeDeploymentActivationViewModel(
    string Url,
    string Email,
    DateTimeOffset ExpiresAt);
