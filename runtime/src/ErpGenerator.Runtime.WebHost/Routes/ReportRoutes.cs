using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.WebHost.Observability;

public static class ReportRoutes
{
    public static IEndpointRouteBuilder MapReportRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/reports", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeReportService reports,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var reportDefinitions = await reports.ListReportsAsync(runtime.Tenant!, cancellationToken);
            return Results.Ok(await FilterReadableReportsAsync(runtime.Tenant!, runtime.User!, reportDefinitions, records, permissions, cancellationToken));
        });

        app.MapGet("/api/runtime/reports/{reportKey}.csv", async (
            string reportKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeReportService reports,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var authorized = await AuthorizeReportAsync(runtime, reports, records, permissions, reportKey, cancellationToken);
            if (authorized.Error is not null)
            {
                return authorized.Error;
            }

            var query = BuildReportQuery(httpContext.Request);
            RuntimeReportResult result;
            try
            {
                result = await reports.ExecuteReportAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    authorized.Report!,
                    authorized.Entity!,
                    query,
                    cancellationToken);
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditReportPermissionDeniedAsync(records, runtime, authorized.Report!, cancellationToken);
                return Results.StatusCode(403);
            }

            await AuditReportViewedAsync(records, runtime, authorized.Report!, cancellationToken);
            await usageEvents.ForwardAsync(
                runtime,
                "report_exported",
                entityKey: authorized.Entity!.Key,
                reportKey: authorized.Report!.Key,
                metadata: new Dictionary<string, object?> { ["sourceEntity"] = authorized.Report.SourceEntity },
                cancellationToken: cancellationToken);

            httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{SanitizeCsvFileName(result.Key)}.csv\"";
            return Results.Text(BuildReportCsv(result), "text/csv; charset=utf-8");
        });

        app.MapGet("/api/runtime/reports/{reportKey}", async (
            string reportKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeReportService reports,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var authorized = await AuthorizeReportAsync(runtime, reports, records, permissions, reportKey, cancellationToken);
            if (authorized.Error is not null)
            {
                return authorized.Error;
            }

            RuntimeReportResult result;
            try
            {
                result = await reports.ExecuteReportAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    authorized.Report!,
                    authorized.Entity!,
                    BuildReportQuery(httpContext.Request),
                    cancellationToken);
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditReportPermissionDeniedAsync(records, runtime, authorized.Report!, cancellationToken);
                return Results.StatusCode(403);
            }

            await AuditReportViewedAsync(records, runtime, authorized.Report!, cancellationToken);
            await usageEvents.ForwardAsync(
                runtime,
                "report_viewed",
                entityKey: authorized.Entity!.Key,
                reportKey: authorized.Report!.Key,
                metadata: new Dictionary<string, object?> { ["sourceEntity"] = authorized.Report.SourceEntity },
                cancellationToken: cancellationToken);
            return Results.Ok(result);
        });

        return app;
    }
}
