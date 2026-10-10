using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Shared.Tenants;

public sealed record RuntimeScheduleTargetsViewModel(
    int Page,
    bool HasNextPage,
    IReadOnlyList<RuntimeTenantEnvironmentViewModel> Targets);
