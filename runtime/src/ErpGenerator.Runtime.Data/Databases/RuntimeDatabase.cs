using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases.Backups;
using ErpGenerator.Runtime.Data.Databases.DeploymentLocks;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITenantDatabaseConnectionFactory connectionFactory;
    private readonly ITenantDatabaseBackupService backupService;
    private readonly TenantDeploymentLockService deploymentLocks;
    private readonly ITenantDatabaseProvisioner provisioner;
    private readonly RuntimeSqlBuilder sqlBuilder;
    private readonly RuntimeLifecycleProductionActivationPolicy productionActivationPolicy;
    private readonly ILogger<RuntimeDatabase>? logger;

    public RuntimeDatabase(
        ITenantDatabaseConnectionFactory connectionFactory,
        RuntimeSqlBuilder sqlBuilder,
        ITenantDatabaseProvisioner? provisioner = null,
        ITenantDatabaseBackupService? backupService = null,
        TenantDeploymentLockService? deploymentLocks = null,
        ILogger<RuntimeDatabase>? logger = null,
        RuntimeLifecycleProductionActivationPolicy? productionActivationPolicy = null)
    {
        this.connectionFactory = connectionFactory;
        this.sqlBuilder = sqlBuilder;
        this.provisioner = provisioner ?? new NoOpTenantDatabaseProvisioner();
        this.backupService = backupService ?? DisabledTenantDatabaseBackupService.Instance;
        this.logger = logger;
        this.productionActivationPolicy = productionActivationPolicy ?? new RuntimeLifecycleProductionActivationPolicy(null, null);
        this.deploymentLocks = deploymentLocks ?? new TenantDeploymentLockService(
            connectionFactory,
            Options.Create(new TenantDeploymentLockOptions()));
    }

    public async Task ApplyCompiledBundleAsync(
        ResolvedTenantEnvironment tenant,
        CompiledBundle bundle,
        CancellationToken cancellationToken = default)
    {
        var plan = new ErpBundleMigrationPlanner().PlanMigration(null, bundle);
        var status = "deployed";
        await ApplyBundleDeploymentAsync(
            tenant,
            bundle,
            plan,
            tenant.Environment.ToString().ToLowerInvariant(),
            status,
            activate: true,
            message: "Bundle applied by runtime compiler.",
            cancellationToken);
    }

    public async Task<RuntimeBundleDeploymentResult> ApplyBundleDeploymentAsync(
        ResolvedTenantEnvironment tenant,
        CompiledBundle bundle,
        BundleMigrationPlan plan,
        string environment,
        string status,
        bool activate,
        string message,
        CancellationToken cancellationToken = default,
        RuntimeLifecyclePrivilegePromotionAuthorization? productionAuthorization = null)
    {
        var productionActivation = IsErpActivation(tenant, activate);
        var normalizedEnvironment = tenant.Environment.ToString().ToLowerInvariant();
        var authoritativeStatus = activate
            ? "deployed"
            : status;
        var operation = "deploy";
        var deploymentLock = await deploymentLocks.AcquireOrThrowAsync(tenant, normalizedEnvironment, operation, cancellationToken);
        logger?.LogInformation(
            "Runtime deployment {Operation} started for tenant {TenantId} environment {Environment}; bundle {BundleId} {BundleVersion} {BundleHash}; lock {LockId}.",
            operation,
            tenant.TenantStableId,
            normalizedEnvironment,
            bundle.BundleId,
            bundle.Version,
            bundle.BundleHash,
            deploymentLock.LockId);
        try
        {
            // A caller may carry an upstream fail-closed policy decision that is
            // intentionally stricter than the schema migration planner. Preserve
            // explicit blocked input; every nonblocked plan is recomputed here
            // and again inside the authoritative mutation transaction.
            if (!plan.IsBlocked)
            {
                var actualActive = await ReadActiveCompiledBundleUnderLockAsync(tenant, cancellationToken);
                plan = new ErpBundleMigrationPlanner().PlanMigration(actualActive, bundle);
                if (productionActivation)
                {
                    EnsureErpActivationAuthorized(tenant, actualActive, bundle, productionAuthorization);
                }
            }

            var result = await ApplyBundleDeploymentCoreAsync(
                tenant,
                bundle,
                plan,
                normalizedEnvironment,
                authoritativeStatus,
                activate,
                message,
                operation,
                deploymentLock,
                cancellationToken,
                productionAuthorization);
            if (result.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(
                    "Runtime deployment {Operation} failed for tenant {TenantId} environment {Environment}; deployment {DeploymentId}; bundle version {BundleVersionId}; migration plan {MigrationPlanId}; bundle {BundleId} {BundleVersion} {BundleHash}.",
                    operation,
                    tenant.TenantStableId,
                    normalizedEnvironment,
                    result.DeploymentId,
                    result.BundleVersionId,
                    result.MigrationPlanId,
                    result.BundleId,
                    result.Version,
                    result.BundleHash);
            }
            else
            {
                logger?.LogInformation(
                    "Runtime deployment {Operation} completed for tenant {TenantId} environment {Environment}; deployment {DeploymentId}; bundle version {BundleVersionId}; migration plan {MigrationPlanId}; bundle {BundleId} {BundleVersion} {BundleHash}; status {Status}.",
                    operation,
                    tenant.TenantStableId,
                    normalizedEnvironment,
                    result.DeploymentId,
                    result.BundleVersionId,
                    result.MigrationPlanId,
                    result.BundleId,
                    result.Version,
                    result.BundleHash,
                    result.Status);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TenantDeploymentLockException)
        {
            logger?.LogError(
                "Runtime deployment {Operation} threw for tenant {TenantId} environment {Environment}; bundle {BundleId} {BundleVersion} {BundleHash}; error type {ErrorType}; error {ErrorMessage}.",
                operation,
                tenant.TenantStableId,
                normalizedEnvironment,
                bundle.BundleId,
                bundle.Version,
                bundle.BundleHash,
                ex.GetType().Name,
                SanitizeLifecycleError(ex.Message));
            var failed = await RecordFailedDeploymentAsync(
                tenant,
                bundle,
                plan,
                normalizedEnvironment,
                operation,
                "apply_bundle",
                ex,
                deploymentLock,
                cancellationToken);
            logger?.LogWarning(
                "Runtime deployment {Operation} failure recorded for tenant {TenantId} environment {Environment}; deployment {DeploymentId}; bundle version {BundleVersionId}; migration plan {MigrationPlanId}.",
                operation,
                tenant.TenantStableId,
                normalizedEnvironment,
                failed.DeploymentId,
                failed.BundleVersionId,
                failed.MigrationPlanId);
            return failed;
        }
        finally
        {
            await deploymentLocks.ReleaseAsync(deploymentLock, CancellationToken.None);
            logger?.LogInformation(
                "Runtime deployment {Operation} lock {LockId} released for tenant {TenantId} environment {Environment}.",
                operation,
                deploymentLock.LockId,
                tenant.TenantStableId,
                normalizedEnvironment);
        }
    }

    private async Task<RuntimeBundleDeploymentResult> ApplyBundleDeploymentCoreAsync(
        ResolvedTenantEnvironment tenant,
        CompiledBundle bundle,
        BundleMigrationPlan plan,
        string environment,
        string status,
        bool activate,
        string message,
        string operation,
        TenantDeploymentLockHandle deploymentLock,
        CancellationToken cancellationToken,
        RuntimeLifecyclePrivilegePromotionAuthorization? productionAuthorization)
    {
        if (plan.IsBlocked)
        {
            logger?.LogWarning(
                "Runtime deployment {Operation} blocked by migration plan for tenant {TenantId} environment {Environment}; bundle {BundleId} {BundleVersion} {BundleHash}; risk {RiskLevel}.",
                operation,
                tenant.TenantStableId,
                environment,
                bundle.BundleId,
                bundle.Version,
                bundle.BundleHash,
                plan.RiskLevel);
            return await RecordBlockedDeploymentAsync(
                tenant,
                bundle,
                plan,
                environment,
                message,
                deploymentLock,
                cancellationToken);
        }

        var activeBeforeDeployment = IsErpActivation(tenant, activate)
            ? await ReadActiveCompiledBundleUnderLockAsync(tenant, cancellationToken)
            : null;
        if (activeBeforeDeployment is not null &&
            !string.Equals(activeBeforeDeployment.BundleHash, bundle.BundleHash, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogInformation(
                "Pre-promotion backup requested for tenant {TenantId} environment {Environment}; bundle {BundleId} {BundleVersion} {BundleHash}.",
                tenant.TenantStableId,
                environment,
                bundle.BundleId,
                bundle.Version,
                bundle.BundleHash);
            var backup = await backupService.CreateBackupAsync(
                tenant,
                "pre-promotion",
                required: false,
                cancellationToken);
            logger?.LogInformation(
                "Pre-promotion backup {BackupId} finished with status {BackupStatus} for tenant {TenantId} environment {Environment}; blocks promotion {BlocksPromotion}.",
                backup.Metadata.BackupId,
                backup.Metadata.Status,
                tenant.TenantStableId,
                environment,
                backup.BlocksPromotion);
            if (backup.BlocksPromotion)
            {
                return await RecordBackupBlockedDeploymentAsync(
                    tenant,
                    bundle,
                    plan,
                    backup,
                    deploymentLock,
                    cancellationToken);
            }
        }

        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        deploymentLock = await deploymentLocks.RenewOrThrowAsync(deploymentLock, cancellationToken);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireDeploymentTransactionFenceAsync(
            connection,
            transaction,
            deploymentLock.LockKey,
            cancellationToken);
        await TenantDeploymentLockService.EnsureHeldAsync(
            connection,
            transaction,
            deploymentLock,
            cancellationToken);
        var actualActive = await ReadActiveCompiledBundleAsync(connection, transaction, cancellationToken);
        plan = new ErpBundleMigrationPlanner().PlanMigration(actualActive, bundle);
        if (plan.IsBlocked)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await RecordBlockedDeploymentAsync(
                tenant,
                bundle,
                plan,
                environment,
                message,
                deploymentLock,
                cancellationToken);
        }

        await EnsureBundleVersionIdentityCompatibleAsync(connection, transaction, bundle, cancellationToken);

        if (IsErpActivation(tenant, activate))
        {
            EnsureErpActivationAuthorized(tenant, actualActive, bundle, productionAuthorization);
            await ConsumeLifecyclePrivilegeEvidenceAsync(
                connection,
                transaction,
                actualActive,
                bundle,
                productionAuthorization,
                cancellationToken);
        }

        await ExecuteBundleStatementsAsync(tenant, connection, transaction, bundle, cancellationToken);

        var previousBundleVersionId = await GetActiveBundleVersionIdAsync(connection, transaction, cancellationToken);
        var bundleVersionId = await GetBundleVersionIdAsync(connection, transaction, bundle, cancellationToken);
        var planId = await InsertMigrationPlanAsync(
            connection,
            transaction,
            previousBundleVersionId,
            bundleVersionId,
            plan,
            plan.IsBlocked ? "blocked" : "applied",
            applied: !plan.IsBlocked,
            cancellationToken);
        await UpdateBundleVersionStatusAsync(connection, transaction, bundleVersionId, status, cancellationToken);

        if (activate)
        {
            if (previousBundleVersionId is not null && previousBundleVersionId.Value != bundleVersionId)
            {
                await UpdateBundleVersionStatusAsync(connection, transaction, previousBundleVersionId.Value, "superseded", cancellationToken);
            }

            await UpsertActiveBundleAsync(connection, transaction, bundleVersionId, previousBundleVersionId, bundle, cancellationToken);
        }

        var deploymentId = await InsertBundleDeploymentAsync(
            connection,
            transaction,
            bundleVersionId,
            planId,
            environment,
            status,
            message,
            new
            {
                bundle.BundleId,
                bundle.Version,
                bundle.BundleHash,
                active = activate,
                plan.RiskLevel,
                operation,
                phase = "metadata_activated"
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new RuntimeBundleDeploymentResult(bundleVersionId, planId, deploymentId, bundle.BundleId, bundle.Version, bundle.BundleHash, status, plan);
    }
    public async Task<RuntimeBundleDeploymentResult> RecordBlockedDeploymentAsync(
        ResolvedTenantEnvironment tenant,
        CompiledBundle bundle,
        BundleMigrationPlan plan,
        string environment,
        string message,
        TenantDeploymentLockHandle deploymentLock,
        CancellationToken cancellationToken = default)
    {
        deploymentLock = await deploymentLocks.RenewOrThrowAsync(deploymentLock, cancellationToken);
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireDeploymentTransactionFenceAsync(connection, transaction, deploymentLock.LockKey, cancellationToken);
        await TenantDeploymentLockService.EnsureHeldAsync(
            connection,
            transaction,
            deploymentLock,
            cancellationToken);
        await EnsureDeploymentLifecycleTablesAsync(connection, transaction, cancellationToken);
        var previousBundleVersionId = await GetActiveBundleVersionIdAsync(connection, transaction, cancellationToken);
        var bundleVersionId = await TryInsertRejectedBundleVersionAsync(connection, transaction, bundle, "failed", cancellationToken);
        var planId = await InsertMigrationPlanAsync(
            connection,
            transaction,
            previousBundleVersionId,
            bundleVersionId,
            plan,
            "blocked",
            applied: false,
            cancellationToken);
        var deploymentId = await InsertBundleDeploymentAsync(
            connection,
            transaction,
            bundleVersionId,
            planId,
            environment,
            "failed",
            message,
            new
            {
                candidate_bundle = DescribeCandidateBundle(bundle),
                candidate_bundle_version_recorded = bundleVersionId is not null,
                candidate_identity_collision = bundleVersionId is null,
                plan.RiskLevel,
                blockedReasons = plan.BlockedReasons
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger?.LogWarning(
            "Blocked runtime deployment recorded for tenant {TenantId} environment {Environment}; deployment {DeploymentId}; bundle version {BundleVersionId}; migration plan {MigrationPlanId}.",
            tenant.TenantStableId,
            environment,
            deploymentId,
            bundleVersionId,
            planId);
        return new RuntimeBundleDeploymentResult(bundleVersionId, planId, deploymentId, bundle.BundleId, bundle.Version, bundle.BundleHash, "failed", plan, "migration_blocked");
    }

    private async Task<RuntimeBundleDeploymentResult> RecordBackupBlockedDeploymentAsync(
        ResolvedTenantEnvironment tenant,
        CompiledBundle bundle,
        BundleMigrationPlan plan,
        TenantDatabaseBackupResult backup,
        TenantDeploymentLockHandle deploymentLock,
        CancellationToken cancellationToken)
    {
        deploymentLock = await deploymentLocks.RenewOrThrowAsync(deploymentLock, cancellationToken);
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireDeploymentTransactionFenceAsync(connection, transaction, deploymentLock.LockKey, cancellationToken);
        await TenantDeploymentLockService.EnsureHeldAsync(
            connection,
            transaction,
            deploymentLock,
            cancellationToken);
        await EnsureDeploymentLifecycleTablesAsync(connection, transaction, cancellationToken);
        var previousBundleVersionId = await GetActiveBundleVersionIdAsync(connection, transaction, cancellationToken);
        var bundleVersionId = await TryInsertRejectedBundleVersionAsync(connection, transaction, bundle, "failed", cancellationToken);
        var planId = await InsertMigrationPlanAsync(
            connection,
            transaction,
            previousBundleVersionId,
            bundleVersionId,
            plan,
            "backup_failed",
            applied: false,
            cancellationToken);
        var deploymentId = await InsertBundleDeploymentAsync(
            connection,
            transaction,
            bundleVersionId,
            planId,
            tenant.Environment.ToString().ToLowerInvariant(),
            "failed",
            "Promotion blocked because the required tenant database backup failed.",
            new
            {
                candidate_bundle = DescribeCandidateBundle(bundle),
                candidate_bundle_version_recorded = bundleVersionId is not null,
                candidate_identity_collision = bundleVersionId is null,
                backup = new
                {
                    backup.Metadata.BackupId,
                    backup.Metadata.Status,
                    backup.Metadata.Reason,
                    backup.Metadata.SizeBytes,
                    backup.Metadata.CreatedAt,
                    backup.Metadata.CompletedAt,
                    backup.Metadata.ErrorMessage
                }
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger?.LogWarning(
            "Backup-blocked promotion recorded for tenant {TenantId} environment {Environment}; deployment {DeploymentId}; bundle version {BundleVersionId}; migration plan {MigrationPlanId}; backup {BackupId}.",
            tenant.TenantStableId,
            tenant.Environment.ToString().ToLowerInvariant(),
            deploymentId,
            bundleVersionId,
            planId,
            backup.Metadata.BackupId);
        return new RuntimeBundleDeploymentResult(bundleVersionId, planId, deploymentId, bundle.BundleId, bundle.Version, bundle.BundleHash, "failed", plan, "backup_failed");
    }

    private static bool IsErpActivation(
        ResolvedTenantEnvironment tenant,
        bool activate) =>
        activate &&
        tenant.Environment == TenantEnvironmentKind.Erp;

    public async Task<RuntimeRollbackResult> RollbackActiveBundleAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken = default,
        RuntimeLifecyclePrivilegePromotionAuthorization? productionAuthorization = null)
    {
        var deploymentLock = await deploymentLocks.AcquireOrThrowAsync(
            tenant,
            tenant.Environment.ToString().ToLowerInvariant(),
            "rollback",
            cancellationToken);
        RuntimeActiveBundlePointer? active = null;
        logger?.LogInformation(
            "Runtime rollback started for tenant {TenantId} environment {Environment}; lock {LockId}.",
            tenant.TenantStableId,
            tenant.Environment.ToString().ToLowerInvariant(),
            deploymentLock.LockId);
        try
        {
            var rollback = await RollbackActiveBundleCoreAsync(
                tenant,
                value => active = value,
                deploymentLock,
                cancellationToken,
                productionAuthorization);
            logger?.LogInformation(
                "Runtime rollback completed for tenant {TenantId} environment {Environment}; deployment {DeploymentId}; from {FromVersion} to {ToVersion}; bundle hash {BundleHash}.",
                tenant.TenantStableId,
                tenant.Environment.ToString().ToLowerInvariant(),
                rollback.DeploymentId,
                rollback.FromVersion,
                rollback.ToVersion,
                rollback.BundleHash);
            return rollback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TenantDeploymentLockException)
        {
            logger?.LogError(
                "Runtime rollback failed for tenant {TenantId} environment {Environment}; active bundle version id {ActiveBundleVersionId}; error type {ErrorType}; error {ErrorMessage}.",
                tenant.TenantStableId,
                tenant.Environment.ToString().ToLowerInvariant(),
                active?.BundleVersionId,
                ex.GetType().Name,
                SanitizeLifecycleError(ex.Message));
            await RecordFailedRollbackAsync(
                tenant,
                active,
                "rollback",
                ex,
                deploymentLock,
                cancellationToken);
            throw;
        }
        finally
        {
            await deploymentLocks.ReleaseAsync(deploymentLock, CancellationToken.None);
            logger?.LogInformation(
                "Runtime rollback lock {LockId} released for tenant {TenantId} environment {Environment}.",
                deploymentLock.LockId,
                tenant.TenantStableId,
                tenant.Environment.ToString().ToLowerInvariant());
        }
    }

    private async Task<RuntimeRollbackResult> RollbackActiveBundleCoreAsync(
        ResolvedTenantEnvironment tenant,
        Action<RuntimeActiveBundlePointer?> setActive,
        TenantDeploymentLockHandle deploymentLock,
        CancellationToken cancellationToken,
        RuntimeLifecyclePrivilegePromotionAuthorization? productionAuthorization)
    {
        deploymentLock = await deploymentLocks.RenewOrThrowAsync(deploymentLock, cancellationToken);
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireDeploymentTransactionFenceAsync(
            connection,
            transaction,
            deploymentLock.LockKey,
            cancellationToken);
        await TenantDeploymentLockService.EnsureHeldAsync(
            connection,
            transaction,
            deploymentLock,
            cancellationToken);
        var active = await ReadActiveBundlePointerAsync(connection, transaction, cancellationToken);
        setActive(active);
        if (active is null)
        {
            throw new InvalidOperationException("No active bundle exists for this tenant environment.");
        }

        if (active.PreviousBundleVersionId is null)
        {
            throw new InvalidOperationException("No previous ERP bundle is available for rollback.");
        }

        var previousBundle = await ReadBundleByIdAsync(connection, transaction, active.PreviousBundleVersionId.Value, cancellationToken)
            ?? throw new InvalidOperationException("Previous bundle artifact was not found.");

        if (tenant.Environment == TenantEnvironmentKind.Erp)
        {
            var currentBundle = await ReadBundleByIdAsync(connection, transaction, active.BundleVersionId, cancellationToken)
                ?? throw new InvalidOperationException("Current bundle artifact was not found.");
            EnsureErpActivationAuthorized(tenant, currentBundle, previousBundle, productionAuthorization);
            await ConsumeLifecyclePrivilegeEvidenceAsync(
                connection,
                transaction,
                currentBundle,
                previousBundle,
                productionAuthorization,
                cancellationToken);
        }

        if (!await IsRollbackableAsync(connection, transaction, active.BundleVersionId, cancellationToken))
        {
            throw new InvalidOperationException("Current bundle was produced by a non-rollbackable migration plan.");
        }

        await ExecuteBundleStatementsAsync(tenant, connection, transaction, previousBundle, cancellationToken);
        await UpdateBundleVersionStatusAsync(connection, transaction, active.BundleVersionId, "rolled_back", cancellationToken);
        await UpdateBundleVersionStatusAsync(connection, transaction, active.PreviousBundleVersionId.Value, "deployed", cancellationToken);
        await UpsertActiveBundleAsync(
            connection,
            transaction,
            active.PreviousBundleVersionId.Value,
            previousBundleVersionId: null,
            previousBundle,
            cancellationToken);
        var deploymentId = await InsertBundleDeploymentAsync(
            connection,
            transaction,
            active.BundleVersionId,
            migrationPlanId: null,
            tenant.Environment.ToString().ToLowerInvariant(),
            "rolled_back",
            $"Rolled back from {active.BundleVersion} to {previousBundle.Version}.",
            new
            {
                from = active.BundleVersion,
                to = previousBundle.Version,
                fromHash = active.BundleHash,
                toHash = previousBundle.BundleHash
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new RuntimeRollbackResult(deploymentId, active.BundleVersion, previousBundle.Version, previousBundle.BundleHash);
    }

    public async Task<IReadOnlyList<RuntimeBundleDeploymentHistoryItem>> ReadDeploymentHistoryAsync(
        ResolvedTenantEnvironment tenant,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionFactory.CreateConnectionString(tenant));
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = new NpgsqlCommand(
                """
                select bd.id,
                       bd.environment,
                       bd.status,
                       bd.started_at,
                       bd.finished_at,
                       bd.error_message,
                       coalesce(
                           bv.bundle_id,
                           bd.metadata #>> '{candidate_bundle,bundleId}',
                           bd.metadata ->> 'bundleId'),
                       coalesce(
                           bv.version,
                           bd.metadata #>> '{candidate_bundle,version}',
                           bd.metadata ->> 'version'),
                       coalesce(
                           bv.bundle_hash,
                           bd.metadata #>> '{candidate_bundle,bundleHash}',
                           bd.metadata ->> 'bundleHash'),
                       mp.risk_level,
                       mp.operations,
                       mp.blocked_reasons
                from _runtime.bundle_deployments bd
                left join _runtime.bundle_versions bv on bv.id = bd.bundle_version_id
                left join _runtime.bundle_migration_plans mp on mp.id = bd.migration_plan_id
                order by bd.started_at desc
                limit @limit
                """,
                connection);
            command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
            var items = new List<RuntimeBundleDeploymentHistoryItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new RuntimeBundleDeploymentHistoryItem(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? [] : JsonSerializer.Deserialize<IReadOnlyList<BundleMigrationOperation>>(reader.GetString(10), JsonOptions) ?? [],
                    reader.IsDBNull(11) ? [] : JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(11), JsonOptions) ?? []));
            }

            return items;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }
}
