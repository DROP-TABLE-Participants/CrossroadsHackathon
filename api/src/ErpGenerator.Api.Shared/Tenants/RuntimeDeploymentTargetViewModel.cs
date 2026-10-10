using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeDeploymentTargetViewModel(
    string TenantId,
    string TenantStableId,
    string Host,
    string Environment,
    string DatabaseName);
