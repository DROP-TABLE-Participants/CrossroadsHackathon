namespace ErpGenerator.Api.Shared.Tenants;

public sealed record CreateTenantViewModel(TenantViewModel Tenant, TenantDeploymentViewModel Deployment);
