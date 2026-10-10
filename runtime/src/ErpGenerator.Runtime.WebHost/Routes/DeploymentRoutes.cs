using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.WebHost.Observability;

public static class DeploymentRoutes
{
    public static IEndpointRouteBuilder MapDeploymentRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/deployment/history", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeDatabaseService runtimeDatabase,
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

            if (!await permissions.IsAllowedAsync(runtime.Tenant!, runtime.User!, "admin", null, cancellationToken))
            {
                await AuditPermissionDeniedAsync(records, runtime, "admin", "deployment", cancellationToken);
                return Results.StatusCode(403);
            }

            return Results.Ok(await runtimeDatabase.ReadDeploymentHistoryAsync(runtime.Tenant!, 50, cancellationToken));
        });

        app.MapPost("/internal/deploy/sample", async (
            HttpContext httpContext,
            DeploySampleRequest request,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            IControlPlaneClient controlPlane,
            ITenantProvisioningService provisioner,
            IRuntimeDatabaseService runtimeDatabase,
            ErpBundleYamlLoader loader,
            ErpBundleCompiler compiler,
            ErpBundleMigrationPlanner planner,
            IRuntimeSeedService seeds,
            IRuntimeBundleTestService bundleTests,
            CancellationToken cancellationToken) =>
        {
            if (!IsInternalRequest(httpContext, configuration))
            {
                return Results.Unauthorized();
            }

            var requestHost = request.Host?.Trim();
            var host = string.IsNullOrWhiteSpace(requestHost)
                ? httpContext.Request.Host.Value
                : requestHost;
            if (string.IsNullOrWhiteSpace(host))
            {
                return Results.BadRequest(new { message = "Host is required.", requestId = RequestId(httpContext) });
            }

            if (!string.IsNullOrWhiteSpace(request.Environment) &&
                !request.Environment.Equals("erp", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Environment must be erp.", requestId = RequestId(httpContext) });
            var targetEnvironment = TenantEnvironmentKind.Erp;
            var tenant = await controlPlane.ResolveTenantEnvironmentAsync(host, targetEnvironment, cancellationToken);
            if (tenant is null)
            {
                return Results.NotFound(new
                {
                    message = $"Tenant {targetEnvironment.ToString().ToLowerInvariant()} environment is not registered in the control plane.",
                    requestId = RequestId(httpContext)
                });
            }

            await controlPlane.RecordDeploymentEventAsync(
                new DeploymentEventCreate(host, tenant.Environment.ToString().ToLowerInvariant(), "deploy-started", $"Sample ERP {request.Version ?? "0.1.0"} staging deployment started."),
                cancellationToken);

            var bundlePath = Path.GetFullPath(ResolveSampleBundlePath(environment.ContentRootPath, request.Version));
            var bundle = await loader.LoadPathAsync(bundlePath, cancellationToken);
            var compiled = compiler.Compile(bundle);
            var productionTenant = tenant;
            var activeProductionBundle = productionTenant is null
                ? null
                : await runtimeDatabase.ReadActiveCompiledBundleAsync(productionTenant, cancellationToken);
            var plan = planner.PlanMigration(activeProductionBundle, compiled);

            await provisioner.ProvisionAsync(tenant, cancellationToken);
            RuntimeBundleDeploymentResult deployment;
            try
            {
                deployment = await runtimeDatabase.ApplyBundleDeploymentAsync(
                    tenant,
                    compiled,
                    plan,
                    tenant.Environment.ToString().ToLowerInvariant(),
                    plan.IsBlocked ? "failed" : "staged",
                    activate: !plan.IsBlocked,
                    message: plan.IsBlocked ? "Sample ERP migration plan was blocked." : "Sample ERP Bundle staged.",
                    cancellationToken);
            }
            catch (TenantDeploymentLockException ex)
            {
                return DeploymentLocked(httpContext, ex);
            }

            if (plan.IsBlocked || deployment.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                var failureMessage = plan.IsBlocked
                    ? "Sample ERP migration plan was blocked."
                    : "Sample ERP staging deployment failed.";
                await controlPlane.RecordDeploymentEventAsync(
                    new DeploymentEventCreate(host, tenant.Environment.ToString().ToLowerInvariant(), "deploy-failed", failureMessage, string.Join("; ", plan.BlockedReasons)),
                    cancellationToken);
                await controlPlane.UpdateDeploymentStatusAsync(
                    new DeploymentStatusUpdate(
                        host,
                        tenant.Environment.ToString().ToLowerInvariant(),
                        "failed",
                        failureMessage,
                        tenant.RuntimeBaseUrl,
                        BundleId: compiled.BundleId,
                        BundleVersion: compiled.Version,
                        BundleHash: compiled.BundleHash),
                    cancellationToken);
                return Results.Conflict(new
                {
                    tenant = tenant.TenantSlug,
                    tenant.Environment,
                    compiled.BundleId,
                    compiled.Version,
                    compiled.BundleHash,
                    deployment.Status,
                    requestId = RequestId(httpContext),
                    deployment.BundleVersionId,
                    deployment.MigrationPlanId,
                    deployment.DeploymentId,
                    plan
                });
            }

            await controlPlane.UpdateDeploymentStatusAsync(
                new DeploymentStatusUpdate(
                    host,
                    tenant.Environment.ToString().ToLowerInvariant(),
                    "staged",
                    "Sample ERP Bundle staged.",
                    tenant.RuntimeBaseUrl,
                    BundleId: compiled.BundleId,
                    BundleVersion: compiled.Version,
                    BundleHash: compiled.BundleHash),
                cancellationToken);

            await controlPlane.RecordDeploymentEventAsync(
                new DeploymentEventCreate(host, tenant.Environment.ToString().ToLowerInvariant(), "deploy-finished", "Sample ERP staging deployment finished."),
                cancellationToken);

            RuntimeSeedResult? seedResult = null;
            RuntimeBundleTestRunResult? testRun = null;
            if (request.ApplySeed || request.RunTests)
            {
                var seedKey = string.IsNullOrWhiteSpace(request.SeedKey) ? "demo" : request.SeedKey.Trim();
                var seed = await loader.LoadSeedFromDirectoryAsync(bundlePath, seedKey, cancellationToken);
                if (seed is null)
                {
                    return Results.BadRequest(new
                    {
                        message = $"Seed '{seedKey}' was not found for bundle '{request.Version}'.",
                        requestId = RequestId(httpContext)
                    });
                }

                if (request.RunTests)
                {
                    var tests = await loader.LoadAcceptanceTestsFromDirectoryAsync(bundlePath, cancellationToken);
                    if (tests.Count == 0)
                    {
                        return Results.BadRequest(new
                        {
                            message = $"No acceptance tests were found for bundle '{request.Version}'.",
                            requestId = RequestId(httpContext)
                        });
                    }

                    testRun = await bundleTests.RunAsync(tenant, compiled.Version, compiled.BundleHash, seed, tests, cancellationToken);
                    await controlPlane.RecordDeploymentEventAsync(
                        new DeploymentEventCreate(host, tenant.Environment.ToString().ToLowerInvariant(), "bundle-tests-finished", $"Bundle acceptance tests {testRun.Status}.", $"{testRun.Passed}/{testRun.Total} passed."),
                        cancellationToken);
                    if (!testRun.Status.Equals("passed", StringComparison.OrdinalIgnoreCase))
                    {
                        await controlPlane.UpdateDeploymentStatusAsync(
                            new DeploymentStatusUpdate(
                                host,
                                tenant.Environment.ToString().ToLowerInvariant(),
                                "failed",
                                "Bundle acceptance tests failed.",
                                tenant.RuntimeBaseUrl,
                                BundleId: compiled.BundleId,
                                BundleVersion: compiled.Version,
                                BundleHash: compiled.BundleHash),
                            cancellationToken);
                        return Results.Conflict(new
                        {
                            tenant = tenant.TenantSlug,
                            tenant.Environment,
                            compiled.BundleId,
                            compiled.Version,
                            compiled.BundleHash,
                            deployment.BundleVersionId,
                            deployment.MigrationPlanId,
                            deployment.DeploymentId,
                            requestId = RequestId(httpContext),
                            deployment.Status,
                            tests = testRun,
                            plan
                        });
                    }
                }
                else
                {
                    seedResult = await seeds.ApplySeedAsync(
                        tenant,
                        new RuntimeUserContext(-1, "system@seed.invalid", RuntimeSystemRoleIds.TenantOwner)
                        {
                            Roles = [RuntimeSystemRoleIds.TenantOwner]
                        },
                        seed,
                        cancellationToken);
                    await controlPlane.RecordDeploymentEventAsync(
                        new DeploymentEventCreate(host, tenant.Environment.ToString().ToLowerInvariant(), "seed-finished", $"Seed {seed.Seed.Key} applied.", $"{seedResult.Created.Count} records created."),
                        cancellationToken);
                }
            }

            return Results.Ok(new
            {
                tenant = tenant.TenantSlug,
                tenant.Environment,
                compiled.BundleId,
                compiled.Version,
                compiled.BundleHash,
                deployment.BundleVersionId,
                deployment.MigrationPlanId,
                deployment.DeploymentId,
                deployment.Status,
                runtimeUrl = tenant.RuntimeBaseUrl,
                seed = seedResult,
                tests = testRun,
                plan
            });
        });

        app.MapPost("/internal/deploy/sample/rollback", async (
            HttpContext httpContext,
            DeploySampleRequest request,
            IConfiguration configuration,
            IControlPlaneClient controlPlane,
            IRuntimeDatabaseService runtimeDatabase,
            CancellationToken cancellationToken) =>
        {
            if (!IsInternalRequest(httpContext, configuration))
            {
                return Results.Unauthorized();
            }

            var host = string.IsNullOrWhiteSpace(request.Host) ? httpContext.Request.Host.Value : request.Host.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                return Results.BadRequest(new { message = "Host is required.", requestId = RequestId(httpContext) });
            }

            var runtimeTenant = await controlPlane.ResolveTenantEnvironmentAsync(host, TenantEnvironmentKind.Erp, cancellationToken);
            if (runtimeTenant is null)
            {
                return Results.NotFound(new
                {
                    message = "Tenant ERP environment is not registered in the control plane.",
                    requestId = RequestId(httpContext)
                });
            }

            try
            {
                var rollback = await runtimeDatabase.RollbackActiveBundleAsync(runtimeTenant, cancellationToken);
                await controlPlane.UpdateDeploymentStatusAsync(
                    new DeploymentStatusUpdate(host, "erp", "rolled_back", $"Rolled back from {rollback.FromVersion} to {rollback.ToVersion}.", runtimeTenant.RuntimeBaseUrl, BundleVersion: rollback.ToVersion, BundleHash: rollback.BundleHash),
                    cancellationToken);
                await controlPlane.RecordDeploymentEventAsync(
                    new DeploymentEventCreate(host, "erp", "rollback-finished", $"Rolled back from {rollback.FromVersion} to {rollback.ToVersion}."),
                    cancellationToken);

                return Results.Ok(new
                {
                    tenant = runtimeTenant.TenantSlug,
                    runtimeTenant.Environment,
                    rollback.DeploymentId,
                    rollback.FromVersion,
                    rollback.ToVersion,
                    rollback.BundleHash
                });
            }
            catch (TenantDeploymentLockException ex)
            {
                return DeploymentLocked(httpContext, ex);
            }
            catch (InvalidOperationException ex)
            {
                await controlPlane.RecordDeploymentEventAsync(
                    new DeploymentEventCreate(host, "erp", "rollback-failed", ex.Message),
                    cancellationToken);
                return Results.Conflict(new { message = ex.Message, requestId = RequestId(httpContext) });
            }
        });

        return app;
    }

    private static string? RequestId(HttpContext httpContext) =>
        RequestCorrelationContext.GetRequestId(httpContext);

    private static IResult DeploymentLocked(HttpContext httpContext, TenantDeploymentLockException exception) =>
        Results.Conflict(new
        {
            error = "deployment_locked",
            message = exception.Message,
            requestId = RequestId(httpContext),
            environment = exception.Environment,
            operation = exception.Operation,
            existingOperation = exception.ExistingOperation,
            expiresAt = exception.ExistingExpiresAt
        });
}
