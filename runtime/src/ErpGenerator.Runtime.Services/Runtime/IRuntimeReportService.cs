using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface IRuntimeReportService
{
    Task<IReadOnlyList<RuntimeReportDefinition>> ListReportsAsync(ResolvedTenantEnvironment tenant, CancellationToken cancellationToken = default);
    Task<RuntimeReportDefinition?> GetReportAsync(ResolvedTenantEnvironment tenant, string reportKey, CancellationToken cancellationToken = default);
    Task<RuntimeDashboardDefinition> GetDashboardAsync(ResolvedTenantEnvironment tenant, CancellationToken cancellationToken = default);
    Task<RuntimeReportResult> ExecuteReportAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, RuntimeReportDefinition report, RuntimeEntityDefinition entity, RuntimeReportQuery query, CancellationToken cancellationToken = default);
}
