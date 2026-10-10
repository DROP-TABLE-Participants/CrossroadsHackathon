using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.ControlPlane;

public sealed record ControlPlaneScheduleTargetsPage(
    int Page,
    bool HasNextPage,
    IReadOnlyList<ResolvedTenantEnvironment> Targets);
