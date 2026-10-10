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

            var targetEnvironment = ParseDeploymentEnvironment(request.Environment) ?? TenantEnvironmentKind.Staging;
            if (targetEnvironment == TenantEnvironmentKind.Production)
            {
                return Results.BadRequest(new
                {
                    message = "The sample deploy route is staging-only; use the production deployment broker for promotion.",
                    requestId = RequestId(httpContext)
                });
            }

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
            var productionTenant = await controlPlane.ResolveTenantEnvironmentAsync(host, TenantEnvironmentKind.Production, cancellationToken);
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

        app.MapPost("/internal/deploy/sample/promote", async (
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
            IRuntimeBundleTestService bundleTests,
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

            var stagingTenant = await controlPlane.ResolveTenantEnvironmentAsync(host, TenantEnvironmentKind.Staging, cancellationToken);
            var productionTenant = await controlPlane.ResolveTenantEnvironmentAsync(host, TenantEnvironmentKind.Production, cancellationToken);
            if (stagingTenant is null || productionTenant is null)
            {
                return Results.NotFound(new
                {
                    message = "Tenant staging and production environments must both exist before promotion.",
                    requestId = RequestId(httpContext)
                });
            }

            var bundlePath = Path.GetFullPath(ResolveSampleBundlePath(environment.ContentRootPath, request.Version));
            var bundle = await loader.LoadPathAsync(bundlePath, cancellationToken);
            var compiled = compiler.Compile(bundle);
            var stagedBundle = await runtimeDatabase.ReadCompiledBundleByVersionAsync(stagingTenant, compiled.Version, cancellationToken);
            if (stagedBundle is null || stagedBundle.BundleHash != compiled.BundleHash)
            {
                return Results.Conflict(new
                {
                    message = $"Bundle {compiled.Version} must be staged before promotion.",
                    requestId = RequestId(httpContext)
                });
            }

            if (Directory.Exists(Path.Combine(bundlePath, "tests")))
            {
                var acceptanceTests = await loader.LoadAcceptanceTestsFromDirectoryAsync(bundlePath, cancellationToken);
                var testSuiteHash = BundleAcceptanceTestContractHasher.Verify(acceptanceTests).SuiteHash;
                if (!await bundleTests.HasPassingResultAsync(
                        stagingTenant,
                        compiled.Version,
                        compiled.BundleHash,
                        testSuiteHash,
                        cancellationToken))
                {
                    await controlPlane.RecordDeploymentEventAsync(
                        new DeploymentEventCreate(host, "production", "deploy-blocked", "Promotion blocked because bundle acceptance tests have not passed."),
                        cancellationToken);
                    return Results.Conflict(new
                    {
                        message = $"Bundle {compiled.Version} must pass staging acceptance tests before promotion.",
                        requestId = RequestId(httpContext)
                    });
                }
            }

            var activeProductionBundle = await runtimeDatabase.ReadActiveCompiledBundleAsync(productionTenant, cancellationToken);
            var plan = planner.PlanMigration(activeProductionBundle, compiled);
            await provisioner.ProvisionAsync(productionTenant, cancellationToken);
            RuntimeBundleDeploymentResult deployment;
            try
            {
                deployment = await runtimeDatabase.ApplyBundleDeploymentAsync(
                    productionTenant,
                    compiled,
                    plan,
                    "production",
                    plan.IsBlocked ? "failed" : "production",
                    activate: !plan.IsBlocked,
                    message: plan.IsBlocked ? "Promotion blocked by migration plan." : "Sample ERP Bundle promoted to production.",
                    cancellationToken);
            }
            catch (TenantDeploymentLockException ex)
            {
                return DeploymentLocked(httpContext, ex);
            }

            if (plan.IsBlocked || deployment.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                var failureMessage = plan.IsBlocked
                    ? "Promotion blocked by migration plan."
                    : deployment.FailureCode?.StartsWith("lifecycle_attestation_", StringComparison.Ordinal) == true
                        ? "Promotion blocked by the production lifecycle privilege policy."
                        : "Promotion blocked because the required production backup failed.";
                var eventType = plan.IsBlocked
                    ? "deploy-failed"
                    : deployment.FailureCode?.StartsWith("lifecycle_attestation_", StringComparison.Ordinal) == true
                        ? "deploy-blocked"
                        : "backup-failed";
                await controlPlane.RecordDeploymentEventAsync(
                    new DeploymentEventCreate(host, "production", eventType, failureMessage, string.Join("; ", plan.BlockedReasons)),
                    cancellationToken);
                await controlPlane.UpdateDeploymentStatusAsync(
                    new DeploymentStatusUpdate(host, "production", "failed", failureMessage, productionTenant.RuntimeBaseUrl, BundleId: compiled.BundleId, BundleVersion: compiled.Version, BundleHash: compiled.BundleHash),
                    cancellationToken);
                return Results.Conflict(new
                {
                    deployment.Status,
                    requestId = RequestId(httpContext),
                    deployment.BundleVersionId,
                    deployment.MigrationPlanId,
                    deployment.DeploymentId,
                    plan
                });
            }

            string? activationUrl = null;
            if (!string.IsNullOrWhiteSpace(request.OwnerEmail))
            {
                var activationToken = await runtimeDatabase.CreateActivationTokenAsync(
                    productionTenant,
                    request.OwnerEmail,
                    TimeSpan.FromDays(7),
                    cancellationToken);
                activationUrl = BuildActivationUrl(productionTenant.RuntimeBaseUrl, activationToken);
            }

            await controlPlane.UpdateDeploymentStatusAsync(
                new DeploymentStatusUpdate(
                    host,
                    "production",
                    "production",
                    "Sample ERP Bundle promoted to production.",
                    productionTenant.RuntimeBaseUrl,
                    activationUrl,
                    compiled.BundleId,
                    compiled.Version,
                    compiled.BundleHash),
                cancellationToken);
            await controlPlane.RecordDeploymentEventAsync(
                new DeploymentEventCreate(host, "production", "deploy-finished", "Sample ERP production promotion finished."),
                cancellationToken);

            return Results.Ok(new
            {
                tenant = productionTenant.TenantSlug,
                productionTenant.Environment,
                compiled.BundleId,
                compiled.Version,
                compiled.BundleHash,
                deployment.BundleVersionId,
                deployment.MigrationPlanId,
                deployment.DeploymentId,
                deployment.Status,
                runtimeUrl = productionTenant.RuntimeBaseUrl,
                activationUrl,
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

            var productionTenant = await controlPlane.ResolveTenantEnvironmentAsync(host, TenantEnvironmentKind.Production, cancellationToken);
            if (productionTenant is null)
            {
                return Results.NotFound(new
                {
                    message = "Tenant production environment is not registered in the control plane.",
                    requestId = RequestId(httpContext)
                });
            }

            try
            {
                var rollback = await runtimeDatabase.RollbackActiveBundleAsync(productionTenant, cancellationToken);
                await controlPlane.UpdateDeploymentStatusAsync(
                    new DeploymentStatusUpdate(host, "production", "rolled_back", $"Rolled back from {rollback.FromVersion} to {rollback.ToVersion}.", productionTenant.RuntimeBaseUrl, BundleVersion: rollback.ToVersion, BundleHash: rollback.BundleHash),
                    cancellationToken);
                await controlPlane.RecordDeploymentEventAsync(
                    new DeploymentEventCreate(host, "production", "rollback-finished", $"Rolled back from {rollback.FromVersion} to {rollback.ToVersion}."),
                    cancellationToken);

                return Results.Ok(new
                {
                    tenant = productionTenant.TenantSlug,
                    productionTenant.Environment,
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
                    new DeploymentEventCreate(host, "production", "rollback-failed", ex.Message),
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
