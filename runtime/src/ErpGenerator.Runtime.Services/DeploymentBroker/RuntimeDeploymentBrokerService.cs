using System.Net;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.DeploymentLocks;
using ErpGenerator.Runtime.Data.Databases.Staging;

namespace ErpGenerator.Runtime.Services.DeploymentBroker;

public sealed class RuntimeDeploymentBrokerService(
    IControlPlaneClient controlPlane,
    ITenantDatabaseProvisioner provisioner,
    RuntimeDatabase runtimeDatabase,
    RuntimeBundleTestRunner bundleTests,
    ITenantStagingOperationFence stagingFence,
    ITenantStagingDatabaseResetter stagingResetter,
    ErpBundleYamlLoader loader,
    ErpBundleCompiler compiler,
    ErpBundleMigrationPlanner planner,
    RuntimeProductionPromotionReceiptIssuer promotionReceiptIssuer,
    IOptions<RuntimeDeploymentBrokerOptions> options,
    ILogger<RuntimeDeploymentBrokerService> logger)
    : IRuntimeDeploymentBrokerService
{
    private static readonly TimeSpan ActivationLifetime = TimeSpan.FromDays(7);

    public async Task<RuntimeStagingDeploymentBrokerResponse> DeployCuratedAsync(RuntimeCuratedDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        // Runtime compiles the API-owned typed files; the usual staging broker still validates and tests the exact artifact.
        var manifest = new RuntimeDeploymentArtifact("", "", "", RuntimeDeploymentArtifactHash.Compute(request.Files), request.Files);
        await using var materialized = await RuntimeDeploymentArtifactMaterializer.CreateAsync(manifest, cancellationToken);
        var compiled = compiler.Compile(await loader.LoadDirectoryAsync(materialized.DirectoryPath, cancellationToken));
        var artifact = manifest with { BundleId = compiled.BundleId, BundleVersion = compiled.Version, BundleHash = compiled.BundleHash };
        return await DeployStagingAsync(new(request.OperationId, request.RunId, request.GenerationProjectId, request.Target, artifact), cancellationToken);
    }

    public async Task<RuntimeGuestEntryResponse> CreateGuestEntryAsync(RuntimeGuestEntryRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.CustomerId, out var customerId) || string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 120)
            throw new RuntimeDeploymentBrokerValidationException("A customer identity is required.");
        var environment = request.Target.Environment == "production" ? TenantEnvironmentKind.Production : TenantEnvironmentKind.Staging;
        var tenant = await ResolveExactTargetAsync(request.Target, environment, cancellationToken);
        if (await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant, cancellationToken) is null)
            throw new RuntimeDeploymentBrokerValidationException("This ERP is not ready yet.");
        var ticket = await runtimeDatabase.CreateGuestEntryAsync(tenant, customerId.ToString("N"), request.DisplayName, TimeSpan.FromMinutes(2), cancellationToken);
        return new(ticket, tenant.RuntimeBaseUrl.TrimEnd('/') + "/api/auth/guest-entry", DateTimeOffset.UtcNow.AddMinutes(2));
    }

    private static readonly HashSet<string> SupportedTestSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        "create_record",
        "update_record",
        BundleAcceptanceStepTypes.BindActorRecord,
        "run_action",
        "start_workflow_action",
        "approve_task",
        "reject_task",
        "mark_notification_read",
        "call_report"
    };

    private static readonly HashSet<string> SupportedTestAssertions = new(StringComparer.OrdinalIgnoreCase)
    {
        "record_exists",
        "record_count",
        "record_field_equals",
        "workflow_status",
        "workflow_instance_count",
        "task_exists",
        "task_count",
        "notification_exists",
        "report_has_rows",
        "report_metric_equals",
        "report_row_count",
        "report_record_absent",
        "dashboard_metric_equals",
        "dashboard_widget_exists",
        "audit_event_exists",
        "permission_allowed"
    };

    public async Task<RuntimeStagingDeploymentBrokerResponse> DeployStagingAsync(
        RuntimeStagingDeploymentBrokerRequest request,
        CancellationToken cancellationToken = default)
    {
        CompiledBundle? compiled = null;
        RuntimeBundleDeploymentResult? deployment = null;
        string? testSuiteHash = null;
        try
        {
            ValidateCommon(request.OperationId, request.RunId, request.GenerationProjectId);
            ValidateArtifactLineage(request.Artifact);
            var tenant = await ResolveExactTargetAsync(
                request.Target,
                TenantEnvironmentKind.Staging,
                cancellationToken);

            await using var materialized = await RuntimeDeploymentArtifactMaterializer.CreateAsync(
                request.Artifact,
                cancellationToken);
            var bundle = await loader.LoadDirectoryAsync(materialized.DirectoryPath, cancellationToken);
            compiled = compiler.Compile(bundle);
            ValidateCompiledLineage(compiled, request.Artifact);

            var tests = await loader.LoadAcceptanceTestsFromDirectoryAsync(materialized.DirectoryPath, cancellationToken);
            ValidateAcceptanceTests(tests, compiled);
            testSuiteHash = BundleAcceptanceTestContractHasher.Verify(tests, compiled).SuiteHash;
            var seedKeys = tests
                .Select(test => test.Setup.Seed?.Trim())
                .Where(seedKey => !string.IsNullOrWhiteSpace(seedKey))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (seedKeys.Length != 1)
            {
                throw new RuntimeDeploymentBrokerValidationException(
                    "Generated staging acceptance tests must reference exactly one sample seed.");
            }

            var seed = await loader.LoadSeedFromDirectoryAsync(materialized.DirectoryPath, seedKeys[0]!, cancellationToken);
            if (seed is null || !string.Equals(seed.Seed.Key, seedKeys[0], StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeDeploymentBrokerValidationException(
                    "The generated sample seed required by staging acceptance tests is unavailable.");
            }

            await using var operationFence = await stagingFence.AcquireAsync(tenant, cancellationToken);
            await provisioner.ProvisionAsync(tenant, cancellationToken);
            var active = await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant, cancellationToken);
            if (active is not null)
            {
                if (!SameBundle(active, compiled))
                {
                    return StagingFailure(
                        request,
                        tenant.RuntimeBaseUrl,
                        compiled,
                        "staging_reset_required",
                        "Staging contains another active bundle and requires a clean reset before deterministic acceptance testing.",
                        testSuiteHash: testSuiteHash);
                }

                if (!await bundleTests.HasPassingResultAsync(
                        tenant,
                        compiled.Version,
                        compiled.BundleHash,
                        testSuiteHash,
                        cancellationToken))
                {
                    return StagingFailure(
                        request,
                        tenant.RuntimeBaseUrl,
                        compiled,
                        "acceptance_failed",
                        "The previous staging attempt has no passing acceptance evidence and requires a clean reset before retry.",
                        testSuiteHash: testSuiteHash);
                }

                var existingDeploymentId = await FindExactDeploymentIdAsync(tenant, compiled, cancellationToken);
                if (existingDeploymentId is null)
                {
                    return StagingFailure(
                        request,
                        tenant.RuntimeBaseUrl,
                        compiled,
                        "deployment_receipt_missing",
                        "The existing staging bundle has no durable deployment receipt.",
                        testSuiteHash: testSuiteHash);
                }

                var existingActivation = await CreateActivationIfRequestedAsync(
                    tenant,
                    request.OwnerEmail,
                    cancellationToken);
                return StagingSuccess(
                    request,
                    tenant.RuntimeBaseUrl,
                    compiled,
                    testSuiteHash,
                    existingDeploymentId,
                    existingActivation,
                    acceptedExisting: true);
            }

            var migrationPlan = planner.PlanMigration(null, compiled);
            deployment = await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                migrationPlan,
                "staging",
                migrationPlan.IsBlocked ? "failed" : "staged",
                activate: !migrationPlan.IsBlocked,
                message: migrationPlan.IsBlocked
                    ? "Runtime broker staging migration plan was blocked."
                    : "Runtime broker staged the generated bundle for acceptance testing.",
                cancellationToken);
            if (migrationPlan.IsBlocked || deployment.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                return StagingFailure(
                    request,
                    tenant.RuntimeBaseUrl,
                    compiled,
                    deployment.FailureCode ?? "deployment_failed",
                    "Customer staging deployment could not be prepared for acceptance testing.",
                    deployment.DeploymentId.ToString(),
                    testSuiteHash);
            }

            RuntimeBundleTestRunResult testRun;
            try
            {
                testRun = await bundleTests.RunAsync(
                    tenant,
                    compiled.Version,
                    compiled.BundleHash,
                    seed,
                    tests,
                    cancellationToken);
            }
            catch (Exception exception) when (RuntimeSeedFailureDiagnostics.TryCreate(exception, out _))
            {
                RuntimeSeedFailureDiagnostics.TryCreate(exception, out var diagnostic);
                logger.LogWarning(
                    exception,
                    "Runtime deployment broker staging seed validation failed for operation {OperationId}, generation project {GenerationProjectId}; seed ref {SeedRef}, entity {Entity}, fields {Fields}.",
                    request.OperationId,
                    request.GenerationProjectId,
                    diagnostic.SeedRef ?? "unknown",
                    diagnostic.Entity ?? "unknown",
                    diagnostic.Fields.Count == 0 ? "unknown" : string.Join(",", diagnostic.Fields));
                return StagingFailure(
                    request,
                    tenant.RuntimeBaseUrl,
                    compiled,
                    diagnostic.FailureCode,
                    diagnostic.SafeError,
                    deployment.DeploymentId.ToString(),
                    testSuiteHash);
            }

            if (!IsCompletePass(testRun, compiled, testSuiteHash))
            {
                return StagingFailure(
                    request,
                    tenant.RuntimeBaseUrl,
                    compiled,
                    "acceptance_failed",
                    "Customer staging acceptance tests did not pass.",
                    deployment.DeploymentId.ToString(),
                    testSuiteHash);
            }

            var activation = await CreateActivationIfRequestedAsync(
                tenant,
                request.OwnerEmail,
                cancellationToken);
            return StagingSuccess(
                request,
                tenant.RuntimeBaseUrl,
                compiled,
                testSuiteHash,
                deployment.DeploymentId.ToString(),
                activation,
                acceptedExisting: false);
        }
        catch (RuntimeDeploymentBrokerValidationException exception)
        {
            return StagingFailure(request, null, compiled, "invalid_request", exception.Message, deployment?.DeploymentId.ToString(), testSuiteHash);
        }
        catch (TenantDeploymentLockException)
        {
            return StagingFailure(request, null, compiled, "deployment_locked", "Customer staging deployment is already in progress.", deployment?.DeploymentId.ToString(), testSuiteHash);
        }
        catch (TenantStagingOperationConflictException)
        {
            return StagingFailure(request, null, compiled, "staging_conflict", "Another customer staging lifecycle operation is already in progress.", deployment?.DeploymentId.ToString(), testSuiteHash);
        }
        catch (PostgresException exception) when (
            deployment is null &&
            exception.SqlState == PostgresErrorCodes.TooManyConnections)
        {
            // No deployment receipt was created, so retrying the exact
            // operation id is safe. Keep this classification deliberately
            // narrow: acceptance or post-deployment failures must retain their
            // normal reset/repair semantics instead of being replayed blindly.
            logger.LogWarning(
                exception,
                "Runtime deployment broker staging preflight {OperationId} temporarily lacked database capacity for generation project {GenerationProjectId}.",
                request.OperationId,
                request.GenerationProjectId);
            return StagingFailure(
                request,
                null,
                compiled,
                RuntimeDeploymentBrokerFailureCodes.StagingPreflightTemporarilyUnavailable,
                "Customer staging deployment is temporarily unavailable.",
                testSuiteHash: testSuiteHash);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Runtime deployment broker staging operation {OperationId} failed for generation project {GenerationProjectId}.",
                request.OperationId,
                request.GenerationProjectId);
            return StagingFailure(request, null, compiled, "deployment_failed", "Customer staging deployment or acceptance testing failed.", deployment?.DeploymentId.ToString(), testSuiteHash);
        }
    }

    public async Task<RuntimeStagingResetBrokerResponse> ResetStagingAsync(
        RuntimeStagingResetBrokerRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateCommon(request.OperationId, request.RunId, request.GenerationProjectId);
            var tenant = await ResolveExactTargetAsync(request.Target, TenantEnvironmentKind.Staging, cancellationToken);
            await stagingResetter.ResetAsync(tenant, cancellationToken);
            return new RuntimeStagingResetBrokerResponse(
                true,
                "reset",
                request.OperationId,
                request.Target,
                tenant.RuntimeBaseUrl);
        }
        catch (RuntimeDeploymentBrokerValidationException exception)
        {
            return ResetFailure(request, "invalid_request", exception.Message);
        }
        catch (TenantStagingOperationConflictException)
        {
            return ResetFailure(request, "staging_conflict", "Another staging lifecycle operation is already in progress.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Runtime deployment broker reset operation {OperationId} failed for generation project {GenerationProjectId}.",
                request.OperationId,
                request.GenerationProjectId);
            return ResetFailure(request, "reset_failed", "Customer staging could not be reset safely.");
        }
    }

    public async Task<RuntimeProductionPromotionBrokerResponse> PromoteProductionAsync(
        RuntimeProductionPromotionBrokerRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            promotionReceiptIssuer.EnsureConfigured();
            ValidateCommon(request.OperationId, request.RunId, request.GenerationProjectId);
            ValidateBundleLineage(request.BundleId, request.BundleVersion, request.BundleHash);
            ValidateTestSuiteHash(request.TestSuiteHash);
            var staging = await ResolveExactTargetAsync(
                request.StagingTarget,
                TenantEnvironmentKind.Staging,
                cancellationToken);
            var production = await ResolveExactTargetAsync(
                request.ProductionTarget,
                TenantEnvironmentKind.Production,
                cancellationToken);
            if (!string.Equals(staging.TenantId, production.TenantId, StringComparison.Ordinal) ||
                !string.Equals(staging.TenantStableId, production.TenantStableId, StringComparison.Ordinal))
            {
                throw new RuntimeDeploymentBrokerValidationException(
                    "Promotion source and target must belong to the same exact tenant.");
            }

            var stagedBundle = await runtimeDatabase.ReadCompiledBundleByVersionAsync(
                staging,
                request.BundleVersion,
                cancellationToken);
            if (stagedBundle is null ||
                !string.Equals(stagedBundle.BundleId, request.BundleId, StringComparison.Ordinal) ||
                !string.Equals(stagedBundle.Version, request.BundleVersion, StringComparison.Ordinal) ||
                !string.Equals(stagedBundle.BundleHash, request.BundleHash, StringComparison.OrdinalIgnoreCase))
            {
                return PromotionFailure(request, production.RuntimeBaseUrl, "lineage_mismatch", "The exact staged bundle was not found for production promotion.");
            }

            if (!await bundleTests.HasPassingResultAsync(
                    staging,
                    stagedBundle.Version,
                    stagedBundle.BundleHash,
                    request.TestSuiteHash,
                    cancellationToken))
            {
                return PromotionFailure(request, production.RuntimeBaseUrl, "acceptance_missing", "The exact staged bundle has no passing acceptance-test evidence.");
            }

            await provisioner.ProvisionAsync(production, cancellationToken);
            var activeProduction = await runtimeDatabase.ReadActiveCompiledBundleAsync(production, cancellationToken);
            if (activeProduction is not null && SameBundle(activeProduction, stagedBundle))
            {
                var existingDeploymentId = await FindExactDeploymentIdAsync(
                    production,
                    stagedBundle,
                    cancellationToken);
                if (existingDeploymentId is null)
                {
                    return PromotionFailure(
                        request,
                        production.RuntimeBaseUrl,
                        "deployment_receipt_missing",
                        "The active production bundle has no durable deployment receipt.",
                        bundle: stagedBundle);
                }

                var existingActivation = await CreateActivationIfRequestedAsync(
                    production,
                    request.OwnerEmail,
                    cancellationToken);
                return PromotionSuccess(
                    request,
                    production.RuntimeBaseUrl,
                    stagedBundle,
                    existingDeploymentId,
                    existingActivation,
                    acceptedExisting: true,
                    promotionReceiptIssuer.Issue(request, existingDeploymentId));
            }

            if (!MatchesExpectedBase(activeProduction, request))
            {
                return PromotionFailure(
                    request,
                    production.RuntimeBaseUrl,
                    "stale_base",
                    "Production has changed since this staging proposal was created. Restage the proposal before applying.");
            }

            var lifecycleAuthorization = RuntimeLifecyclePrivilegePromotionAuthorization.FromBrokerRequest(request);
            var lifecyclePrivilege = ValidateLifecyclePrivilegePromotion(
                activeProduction,
                stagedBundle,
                request,
                options.Value.LifecycleMachineAttestationSecret,
                options.Value.LifecycleSupervisorAttestationSecret);
            if (!lifecyclePrivilege.IsValid)
            {
                return PromotionFailure(
                    request,
                    production.RuntimeBaseUrl,
                    lifecyclePrivilege.FailureCode!,
                    lifecyclePrivilege.SafeError!,
                    bundle: stagedBundle);
            }

            var migrationPlan = planner.PlanMigration(activeProduction, stagedBundle);
            var deployment = await runtimeDatabase.ApplyBundleDeploymentAsync(
                production,
                stagedBundle,
                migrationPlan,
                "production",
                migrationPlan.IsBlocked ? "failed" : "production",
                activate: !migrationPlan.IsBlocked,
                message: migrationPlan.IsBlocked
                    ? "Runtime broker production promotion migration plan was blocked."
                    : "Runtime broker promoted the generated bundle to production.",
                cancellationToken,
                lifecycleAuthorization);
            if (migrationPlan.IsBlocked || deployment.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                var failureCode = deployment.FailureCode ?? "deployment_failed";
                var safeError = deployment.SafeError ?? (failureCode == "backup_failed"
                    ? "Production promotion was blocked because the required database backup failed."
                    : "Customer production promotion failed.");
                return PromotionFailure(
                    request,
                    production.RuntimeBaseUrl,
                    failureCode,
                    safeError,
                    deployment.DeploymentId.ToString(),
                    stagedBundle);
            }

            var activation = await CreateActivationIfRequestedAsync(
                production,
                request.OwnerEmail,
                cancellationToken);
            return PromotionSuccess(
                request,
                production.RuntimeBaseUrl,
                stagedBundle,
                deployment.DeploymentId.ToString(),
                activation,
                acceptedExisting: false,
                promotionReceiptIssuer.Issue(request, deployment.DeploymentId.ToString()));
        }
        catch (RuntimeDeploymentBrokerValidationException exception)
        {
            return PromotionFailure(request, null, "invalid_request", exception.Message);
        }
        catch (TenantDeploymentLockException)
        {
            return PromotionFailure(request, null, "deployment_locked", "Customer production promotion is already in progress.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Runtime deployment broker production operation {OperationId} failed for generation project {GenerationProjectId}.",
                request.OperationId,
                request.GenerationProjectId);
            return PromotionFailure(request, null, "deployment_failed", "Customer production promotion failed.");
        }
    }

    public async Task<RuntimeOwnerActivationBrokerResponse> CreateOwnerActivationAsync(
        RuntimeOwnerActivationBrokerRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateCommon(request.OperationId, request.RunId, request.GenerationProjectId);
            ValidateEmail(request.OwnerEmail);
            var kind = ParseEnvironment(request.Target.Environment);
            var tenant = await ResolveExactTargetAsync(request.Target, kind, cancellationToken);
            var activation = await CreateActivationIfRequestedAsync(tenant, request.OwnerEmail, cancellationToken)
                ?? throw new RuntimeDeploymentBrokerValidationException("Owner email is required before creating an activation link.");
            return new RuntimeOwnerActivationBrokerResponse(
                true,
                "pending",
                request.OperationId,
                request.Target,
                tenant.RuntimeBaseUrl,
                activation);
        }
        catch (RuntimeDeploymentBrokerValidationException exception)
        {
            return ActivationFailure(request, "invalid_request", exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Runtime deployment broker activation operation {OperationId} failed for generation project {GenerationProjectId}.",
                request.OperationId,
                request.GenerationProjectId);
            return ActivationFailure(request, "activation_failed", "Runtime owner activation link could not be created.");
        }
    }

    private async Task<ResolvedTenantEnvironment> ResolveExactTargetAsync(
        RuntimeDeploymentTarget target,
        TenantEnvironmentKind kind,
        CancellationToken cancellationToken)
    {
        ValidateTarget(target, kind);
        var resolved = await controlPlane.ResolveTenantEnvironmentAsync(target.Host, kind, cancellationToken);
        if (resolved is null ||
            resolved.Environment != kind ||
            !string.Equals(resolved.TenantId, target.TenantId, StringComparison.Ordinal) ||
            !string.Equals(resolved.TenantStableId, target.TenantStableId, StringComparison.Ordinal) ||
            !string.Equals(resolved.Host, target.Host, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.DatabaseName, target.DatabaseName, StringComparison.Ordinal))
        {
            throw new RuntimeDeploymentBrokerValidationException(
                "Deployment target lineage no longer matches the Runtime control-plane resolution.");
        }

        return resolved;
    }

    private async Task<RuntimeDeploymentActivation?> CreateActivationIfRequestedAsync(
        ResolvedTenantEnvironment tenant,
        string? ownerEmail,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ownerEmail))
        {
            return null;
        }

        ValidateEmail(ownerEmail);
        var normalizedEmail = ownerEmail.Trim();
        await runtimeDatabase.ExpirePendingOwnerActivationTokensAsync(tenant, normalizedEmail, cancellationToken);
        var token = await runtimeDatabase.CreateActivationTokenAsync(
            tenant,
            normalizedEmail,
            ActivationLifetime,
            cancellationToken);
        var expiresAt = DateTimeOffset.UtcNow.Add(ActivationLifetime);
        return new RuntimeDeploymentActivation(
            $"{tenant.RuntimeBaseUrl.TrimEnd('/')}/activate?token={WebUtility.UrlEncode(token)}",
            normalizedEmail,
            expiresAt);
    }

    private async Task<string?> FindExactDeploymentIdAsync(
        ResolvedTenantEnvironment tenant,
        CompiledBundle bundle,
        CancellationToken cancellationToken)
    {
        var history = await runtimeDatabase.ReadDeploymentHistoryAsync(tenant, 200, cancellationToken);
        return history.FirstOrDefault(item =>
            !item.Status.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.BundleId, bundle.BundleId, StringComparison.Ordinal) &&
            string.Equals(item.BundleVersion, bundle.Version, StringComparison.Ordinal) &&
            string.Equals(item.BundleHash, bundle.BundleHash, StringComparison.OrdinalIgnoreCase))?.Id.ToString();
    }

    private static void ValidateCommon(string operationId, string runId, string generationProjectId)
    {
        ValidateIdentifier(operationId, "operationId");
        ValidateIdentifier(runId, "runId");
        ValidateIdentifier(generationProjectId, "generationProjectId");
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > RuntimeDeploymentBrokerLimits.MaximumIdentifierLength ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.')))
        {
            throw new RuntimeDeploymentBrokerValidationException($"{name} is invalid.");
        }
    }

    private static void ValidateTarget(RuntimeDeploymentTarget target, TenantEnvironmentKind kind)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateIdentifier(target.TenantId, "tenantId");
        ValidateIdentifier(target.TenantStableId, "tenantStableId");
        if (string.IsNullOrWhiteSpace(target.Host) || target.Host.Length > 253 ||
            Uri.CheckHostName(target.Host) == UriHostNameType.Unknown)
        {
            throw new RuntimeDeploymentBrokerValidationException("target host is invalid.");
        }

        if (!string.Equals(target.Environment, kind.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeDeploymentBrokerValidationException(
                $"Deployment target must be the exact {kind.ToString().ToLowerInvariant()} environment.");
        }

        if (string.IsNullOrWhiteSpace(target.DatabaseName) || target.DatabaseName.Length > 128)
        {
            throw new RuntimeDeploymentBrokerValidationException("target database name is invalid.");
        }
    }

    private static void ValidateArtifactLineage(RuntimeDeploymentArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateBundleLineage(artifact.BundleId, artifact.BundleVersion, artifact.BundleHash);
        if (!IsSha256(artifact.ArtifactHash))
        {
            throw new RuntimeDeploymentBrokerValidationException("artifactHash must be a SHA-256 value.");
        }
    }

    private static void ValidateBundleLineage(string bundleId, string bundleVersion, string bundleHash)
    {
        ValidateIdentifier(bundleId, "bundleId");
        if (string.IsNullOrWhiteSpace(bundleVersion) || bundleVersion.Length > 64)
        {
            throw new RuntimeDeploymentBrokerValidationException("bundleVersion is invalid.");
        }

        if (!IsSha256(bundleHash))
        {
            throw new RuntimeDeploymentBrokerValidationException("bundleHash must be a SHA-256 value.");
        }
    }

    private static void ValidateTestSuiteHash(string testSuiteHash)
    {
        if (!IsSha256(testSuiteHash))
        {
            throw new RuntimeDeploymentBrokerValidationException("testSuiteHash must be an exact SHA-256 value.");
        }
    }

    private static void ValidateCompiledLineage(CompiledBundle compiled, RuntimeDeploymentArtifact artifact)
    {
        if (!string.Equals(compiled.BundleId, artifact.BundleId, StringComparison.Ordinal) ||
            !string.Equals(compiled.Version, artifact.BundleVersion, StringComparison.Ordinal) ||
            !string.Equals(compiled.BundleHash, artifact.BundleHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeDeploymentBrokerValidationException(
                "Compiled bundle lineage does not match the hash-bound artifact request.");
        }
    }

    internal static void ValidateAcceptanceTests(IReadOnlyList<BundleAcceptanceTestDocument> tests)
    {
        if (tests.Count == 0)
        {
            throw new RuntimeDeploymentBrokerValidationException(
                "Generated staging acceptance tests are required before customer review.");
        }

        foreach (var test in tests)
        {
            var workflowStartSteps = new Dictionary<string, BundleAcceptanceStep>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(test.Test.Key) ||
                string.IsNullOrWhiteSpace(test.Setup.Seed) ||
                test.Assertions.Count == 0 ||
                test.Steps.Any(step => !SupportedTestSteps.Contains(step.Type)) ||
                test.Assertions.Any(assertion => !SupportedTestAssertions.Contains(assertion.Type)))
            {
                throw new RuntimeDeploymentBrokerValidationException(
                    "Generated staging acceptance tests contain unsupported or incomplete typed operations.");
            }

            foreach (var step in test.Steps)
            {
                if (test.Schema.Equals(BundleAcceptanceTestSchemas.V3, StringComparison.Ordinal) &&
                    step.Type.Equals("reject_task", StringComparison.OrdinalIgnoreCase) &&
                    BundleAcceptanceExpectedOutcomeContract.IsSuccess(step))
                {
                    throw new RuntimeDeploymentBrokerValidationException(
                        $"Generated staging v3 reject_task step '{step.Id}' must expect workflow_rejected with code '{BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected}' and an exact approval-task target.");
                }

                if (step.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase) &&
                    BundleAcceptanceExpectedOutcomeContract.IsSuccess(step))
                {
                    workflowStartSteps[step.Id] = step;
                    continue;
                }

                if (!step.Type.Equals("approve_task", StringComparison.OrdinalIgnoreCase) &&
                    !step.Type.Equals("reject_task", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(step.WorkflowRef) ||
                    !workflowStartSteps.TryGetValue(step.WorkflowRef, out var startStep) ||
                    !string.Equals(startStep.Entity, step.Entity, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(startStep.Record?.Ref, step.Record?.Ref, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RuntimeDeploymentBrokerValidationException(
                        "Generated staging approval and rejection steps must reference the exact preceding workflow start for the same record.");
                }


                if (test.Schema.Equals(BundleAcceptanceTestSchemas.V3, StringComparison.Ordinal) &&
                    step.Type.Equals("reject_task", StringComparison.OrdinalIgnoreCase) &&
                    step.Expected is
                    {
                        Outcome: BundleAcceptanceExpectedOutcomes.WorkflowRejected,
                        Code: BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected
                    })
                {
                    var rejectionIndex = test.Steps.IndexOf(step);
                    var priorStepId = rejectionIndex > 0
                        ? test.Steps[rejectionIndex - 1].Id
                        : string.Empty;
                    if (!test.Assertions.Any(assertion =>
                            assertion.Type.Equals("task_exists", StringComparison.OrdinalIgnoreCase) &&
                            assertion.AfterStep.Equals(priorStepId, StringComparison.Ordinal) &&
                            assertion.Entity.Equals(step.Entity, StringComparison.Ordinal) &&
                            string.Equals(assertion.Record?.Ref, step.Record?.Ref, StringComparison.Ordinal) &&
                            assertion.WorkflowRef.Equals(step.WorkflowRef, StringComparison.Ordinal) &&
                            assertion.TaskStep.Equals(step.Expected.Target, StringComparison.Ordinal) &&
                            assertion.Status.Equals("pending", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new RuntimeDeploymentBrokerValidationException(
                            $"Generated staging v3 reject_task step '{step.Id}' requires an exact pending task assertion for expected target '{step.Expected.Target}' at its immediately preceding checkpoint.");
                    }
                }
            }

            foreach (var assertion in test.Assertions.Where(assertion =>
                         assertion.Type.Equals("notification_exists", StringComparison.OrdinalIgnoreCase)))
            {
                var exactStart = workflowStartSteps.GetValueOrDefault(assertion.WorkflowRef);
                var startIndex = exactStart is null ? -1 : test.Steps.IndexOf(exactStart);
                var checkpointIndex = test.Steps.FindIndex(step =>
                    step.Id.Equals(assertion.AfterStep, StringComparison.Ordinal));
                if (!string.Equals(test.Schema, BundleAcceptanceTestSchemas.V3, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(assertion.Entity) ||
                    string.IsNullOrWhiteSpace(assertion.Record?.Ref) ||
                    string.IsNullOrWhiteSpace(assertion.WorkflowRef) ||
                    string.IsNullOrWhiteSpace(assertion.TaskStep) ||
                    string.IsNullOrWhiteSpace(assertion.AfterStep) ||
                    assertion.AfterStep.Equals(BundleAcceptanceAssertionCheckpoints.Setup, StringComparison.Ordinal) ||
                    exactStart is null ||
                    !exactStart.Id.Equals(assertion.WorkflowRef, StringComparison.Ordinal) ||
                    !string.Equals(exactStart.Entity, assertion.Entity, StringComparison.Ordinal) ||
                    !string.Equals(exactStart.Record?.Ref, assertion.Record?.Ref, StringComparison.Ordinal) ||
                    checkpointIndex < startIndex)
                {
                    throw new RuntimeDeploymentBrokerValidationException(
                        "Generated staging notification_exists assertions require exact v3 entity, record ref, workflow ref, notification step, and causal after_step bindings.");
                }
            }

            foreach (var assertion in test.Assertions.Where(assertion =>
                         assertion.Type.Equals("workflow_instance_count", StringComparison.OrdinalIgnoreCase)))
            {
                var start = test.Steps.FirstOrDefault(step =>
                    step.Id.Equals(assertion.AfterStep, StringComparison.Ordinal) &&
                    step.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(test.Schema, BundleAcceptanceTestSchemas.V3, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(assertion.Entity) ||
                    string.IsNullOrWhiteSpace(assertion.Record?.Ref) ||
                    string.IsNullOrWhiteSpace(assertion.Workflow) ||
                    !string.IsNullOrWhiteSpace(assertion.WorkflowRef) ||
                    assertion.Count is null or < 0 ||
                    start is null ||
                    !string.Equals(start.Entity, assertion.Entity, StringComparison.Ordinal) ||
                    !string.Equals(start.Record?.Ref, assertion.Record?.Ref, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(start.Action))
                {
                    throw new RuntimeDeploymentBrokerValidationException(
                        "Generated staging workflow_instance_count assertions require exact v3 entity, record ref, workflow id, non-negative count, an empty workflow_ref, and their exact start_workflow_action as after_step.");
                }
            }
        }

        var businessCoverageError = BundleAcceptanceBusinessCoveragePolicy.FindUnsupportedReason(tests);
        if (businessCoverageError is not null)
        {
            throw new RuntimeDeploymentBrokerValidationException(
                businessCoverageError);
        }
    }

    internal static void ValidateAcceptanceTests(
        IReadOnlyList<BundleAcceptanceTestDocument> tests,
        CompiledBundle compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ValidateAcceptanceTests(tests);
        var unsupported = BundleAcceptanceActorRolePolicy.FindUnsupportedReason(
            tests,
            compiled.Roles.Select(role => role.Id));
        if (unsupported is not null)
        {
            throw new RuntimeDeploymentBrokerValidationException(unsupported);
        }

        var producerContractError = BundleAcceptanceRecordProducerContract.FindUnsupportedReason(tests, compiled);
        if (producerContractError is not null)
        {
            throw new RuntimeDeploymentBrokerValidationException(producerContractError);
        }

        var entityIds = compiled.Entities.Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal);
        var nonCanonicalBind = tests.SelectMany(test => test.Steps)
            .FirstOrDefault(step =>
                step.Type.Equals(BundleAcceptanceStepTypes.BindActorRecord, StringComparison.OrdinalIgnoreCase) &&
                !entityIds.Contains(step.Entity));
        if (nonCanonicalBind is not null)
        {
            throw new RuntimeDeploymentBrokerValidationException(
                $"Acceptance bind_actor_record step '{nonCanonicalBind.Id}' must use an exact canonical compiled entity id.");
        }

        var compiledWorkflows = compiled.Workflows
            .Select(workflow => (workflow.Entity, workflow.Id))
            .ToHashSet();
        var unknownWorkflowInstanceCount = tests.SelectMany(test => test.Assertions)
            .FirstOrDefault(assertion =>
                assertion.Type.Equals("workflow_instance_count", StringComparison.OrdinalIgnoreCase) &&
                !compiledWorkflows.Contains((assertion.Entity, assertion.Workflow)));
        if (unknownWorkflowInstanceCount is not null)
        {
            throw new RuntimeDeploymentBrokerValidationException(
                $"Acceptance workflow_instance_count assertion '{unknownWorkflowInstanceCount.Id}' must reference an exact workflow emitted by the compiled bundle for entity '{unknownWorkflowInstanceCount.Entity}'.");
        }


        foreach (var test in tests.Where(test =>
                     test.Schema.Equals(BundleAcceptanceTestSchemas.V3, StringComparison.Ordinal)))
        {
            foreach (var rejection in test.Steps.Where(step =>
                         step.Type.Equals("reject_task", StringComparison.OrdinalIgnoreCase) &&
                         step.Expected is
                         {
                             Outcome: BundleAcceptanceExpectedOutcomes.WorkflowRejected,
                             Code: BundleAcceptanceExpectedOutcomeCodes.WorkflowTaskRejected
                         }))
            {
                var start = test.Steps.FirstOrDefault(step =>
                    step.Id.Equals(rejection.WorkflowRef, StringComparison.Ordinal) &&
                    step.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase));
                var workflows = start is null
                    ? []
                    : compiled.Workflows.Where(workflow =>
                            workflow.Entity.Equals(start.Entity, StringComparison.Ordinal) &&
                            workflow.Trigger?.Action.Equals(start.Action, StringComparison.Ordinal) == true)
                        .ToArray();
                if (workflows is not [var workflow] ||
                    workflow.OnRejected.Count == 0 ||
                    !FlattenWorkflowSteps(workflow.Steps).Any(step =>
                        step.Type.Equals("approval", StringComparison.OrdinalIgnoreCase) &&
                        step.Id.Equals(rejection.Expected.Target, StringComparison.Ordinal)))
                {
                    throw new RuntimeDeploymentBrokerValidationException(
                        $"Generated staging v3 reject_task step '{rejection.Id}' expected target '{rejection.Expected.Target}' must be an exact approval step in its uniquely referenced compiled rejection workflow.");
                }

                var rejectionProofError = BundleAcceptanceWorkflowRejectionProofPolicy.FindUnsupportedReason(
                    test,
                    rejection,
                    workflow,
                    compiled.Entities);
                if (rejectionProofError is not null)
                {
                    throw new RuntimeDeploymentBrokerValidationException(rejectionProofError);
                }
            }
        }
    }

    private static IEnumerable<WorkflowStepDefinition> FlattenWorkflowSteps(
        IEnumerable<WorkflowStepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var nested in FlattenWorkflowSteps(step.Then))
            {
                yield return nested;
            }
        }
    }

    private static void ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320 ||
            !System.Net.Mail.MailAddress.TryCreate(email.Trim(), out var parsed) ||
            !string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeDeploymentBrokerValidationException("ownerEmail is invalid.");
        }
    }

    private static TenantEnvironmentKind ParseEnvironment(string value) =>
        value.Equals("production", StringComparison.OrdinalIgnoreCase)
            ? TenantEnvironmentKind.Production
            : value.Equals("staging", StringComparison.OrdinalIgnoreCase)
                ? TenantEnvironmentKind.Staging
                : throw new RuntimeDeploymentBrokerValidationException(
                    "Activation environment must be staging or production.");

    private static bool MatchesExpectedBase(
        CompiledBundle? active,
        RuntimeProductionPromotionBrokerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedBaseBundleVersion) &&
            string.IsNullOrWhiteSpace(request.ExpectedBaseBundleHash))
        {
            return true;
        }

        if (active is null)
        {
            return string.IsNullOrWhiteSpace(request.ExpectedBaseBundleVersion) &&
                   string.Equals(
                       request.ExpectedBaseBundleHash,
                       RuntimeLifecyclePrivilegePromotionRules.NoActiveProductionBundleHash,
                       StringComparison.OrdinalIgnoreCase);
        }

        return (string.IsNullOrWhiteSpace(request.ExpectedBaseBundleVersion) ||
                string.Equals(active.Version, request.ExpectedBaseBundleVersion, StringComparison.Ordinal)) &&
               (string.IsNullOrWhiteSpace(request.ExpectedBaseBundleHash) ||
                string.Equals(active.BundleHash, request.ExpectedBaseBundleHash, StringComparison.OrdinalIgnoreCase));
    }

    internal static RuntimeLifecyclePrivilegeAttestationValidationResult ValidateLifecyclePrivilegePromotion(
        CompiledBundle? activeProduction,
        CompiledBundle stagedCandidate,
        RuntimeProductionPromotionBrokerRequest request,
        string? lifecycleMachineAttestationSecret,
        string? lifecycleSupervisorAttestationSecret,
        TimeProvider? timeProvider = null)
    {
        var policy = new RuntimeLifecycleProductionActivationPolicy(
            lifecycleMachineAttestationSecret,
            lifecycleSupervisorAttestationSecret,
            timeProvider);
        return policy.Validate(
            activeProduction,
            stagedCandidate,
            RuntimeLifecyclePrivilegePromotionAuthorization.FromBrokerRequest(request),
            request.ProductionTarget.TenantStableId,
            RuntimeLifecyclePrivilegePromotionAuthorization.ComputeEnvironmentIdentity(request.ProductionTarget));
    }

    private static bool SameBundle(CompiledBundle left, CompiledBundle right) =>
        string.Equals(left.BundleId, right.BundleId, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.BundleHash, right.BundleHash, StringComparison.OrdinalIgnoreCase);

    private static bool IsCompletePass(
        RuntimeBundleTestRunResult testRun,
        CompiledBundle compiled,
        string testSuiteHash) =>
        testRun.Total > 0 &&
        testRun.Passed == testRun.Total &&
        testRun.Failed == 0 &&
        string.Equals(testRun.Status, "passed", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(testRun.BundleVersion, compiled.Version, StringComparison.Ordinal) &&
        string.Equals(testRun.BundleHash, compiled.BundleHash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(testRun.TestSuiteHash, testSuiteHash, StringComparison.Ordinal);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static RuntimeStagingDeploymentBrokerResponse StagingSuccess(
        RuntimeStagingDeploymentBrokerRequest request,
        string runtimeUrl,
        CompiledBundle compiled,
        string testSuiteHash,
        string? deploymentId,
        RuntimeDeploymentActivation? activation,
        bool acceptedExisting) =>
        new(
            true,
            "ready",
            request.OperationId,
            request.Target,
            runtimeUrl,
            compiled.BundleId,
            compiled.Version,
            compiled.BundleHash,
            deploymentId,
            "passed",
            compiled.Version,
            compiled.BundleHash,
            testSuiteHash,
            activation,
            acceptedExisting);

    private static RuntimeStagingDeploymentBrokerResponse StagingFailure(
        RuntimeStagingDeploymentBrokerRequest request,
        string? runtimeUrl,
        CompiledBundle? compiled,
        string failureCode,
        string safeError,
        string? deploymentId = null,
        string? testSuiteHash = null) =>
        new(
            false,
            "failed",
            request.OperationId,
            request.Target,
            runtimeUrl,
            compiled?.BundleId ?? request.Artifact.BundleId,
            compiled?.Version ?? request.Artifact.BundleVersion,
            compiled?.BundleHash ?? request.Artifact.BundleHash,
            deploymentId,
            "failed",
            compiled?.Version ?? request.Artifact.BundleVersion,
            compiled?.BundleHash ?? request.Artifact.BundleHash,
            TestSuiteHash: testSuiteHash,
            FailureCode: failureCode,
            SafeError: safeError);

    private static RuntimeStagingResetBrokerResponse ResetFailure(
        RuntimeStagingResetBrokerRequest request,
        string failureCode,
        string safeError) =>
        new(
            false,
            "reset_failed",
            request.OperationId,
            request.Target,
            FailureCode: failureCode,
            SafeError: safeError);

    private static RuntimeProductionPromotionBrokerResponse PromotionSuccess(
        RuntimeProductionPromotionBrokerRequest request,
        string runtimeUrl,
        CompiledBundle bundle,
        string? deploymentId,
        RuntimeDeploymentActivation? activation,
        bool acceptedExisting,
        RuntimeProductionPromotionReceipt promotionReceipt) =>
        new(
            true,
            "ready",
            request.OperationId,
            request.StagingTarget,
            request.ProductionTarget,
            runtimeUrl,
            bundle.BundleId,
            bundle.Version,
            bundle.BundleHash,
            request.TestSuiteHash,
            deploymentId,
            activation,
            acceptedExisting,
            PromotionReceipt: promotionReceipt);

    private static RuntimeProductionPromotionBrokerResponse PromotionFailure(
        RuntimeProductionPromotionBrokerRequest request,
        string? runtimeUrl,
        string failureCode,
        string safeError,
        string? deploymentId = null,
        CompiledBundle? bundle = null) =>
        new(
            false,
            failureCode == "stale_base" ? "blocked_stale_base" : "failed",
            request.OperationId,
            request.StagingTarget,
            request.ProductionTarget,
            runtimeUrl,
            bundle?.BundleId ?? request.BundleId,
            bundle?.Version ?? request.BundleVersion,
            bundle?.BundleHash ?? request.BundleHash,
            request.TestSuiteHash,
            deploymentId,
            FailureCode: failureCode,
            SafeError: safeError);

    private static RuntimeOwnerActivationBrokerResponse ActivationFailure(
        RuntimeOwnerActivationBrokerRequest request,
        string failureCode,
        string safeError) =>
        new(
            false,
            "failed",
            request.OperationId,
            request.Target,
            FailureCode: failureCode,
            SafeError: safeError);
}
