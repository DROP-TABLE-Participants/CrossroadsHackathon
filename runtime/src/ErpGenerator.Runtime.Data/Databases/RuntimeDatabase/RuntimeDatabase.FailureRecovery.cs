using Microsoft.Extensions.Logging;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases.DeploymentLocks;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    private async Task<RuntimeBundleDeploymentResult> RecordFailedDeploymentAsync(
        ResolvedTenantEnvironment tenant,
        CompiledBundle bundle,
        BundleMigrationPlan plan,
        string environment,
        string operation,
        string phase,
        Exception exception,
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
            "failed",
            applied: false,
            cancellationToken);
        var deploymentId = await InsertBundleDeploymentAsync(
            connection,
            transaction,
            bundleVersionId,
            planId,
            environment,
            "failed",
            SanitizeLifecycleError(exception.Message),
            new
            {
                operation,
                phase,
                failed_step = phase,
                active_bundle_before = previousBundleVersionId,
                candidate_bundle = DescribeCandidateBundle(bundle),
                candidate_bundle_version_recorded = bundleVersionId is not null,
                candidate_identity_collision = bundleVersionId is null,
                physical_apply_may_have_occurred = false,
                metadata_activated = false,
                recovery_hint = "Active metadata was not changed. Additive physical artifacts are inactive unless active metadata references them."
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        logger?.LogWarning(
            "Failed runtime deployment metadata recorded for tenant {TenantId} environment {Environment}; deployment {DeploymentId}; bundle version {BundleVersionId}; migration plan {MigrationPlanId}; operation {Operation}; phase {Phase}.",
            tenant.TenantStableId,
            environment,
            deploymentId,
            bundleVersionId,
            planId,
            operation,
            phase);
        var lifecycleRejection = exception as RuntimeLifecyclePrivilegePromotionRejectedException;
        var identityConflict = exception as RuntimeBundleVersionIdentityConflictException;
        return new RuntimeBundleDeploymentResult(
            bundleVersionId,
            planId,
            deploymentId,
            bundle.BundleId,
            bundle.Version,
            bundle.BundleHash,
            "failed",
            plan,
            lifecycleRejection?.FailureCode ??
                (identityConflict is null ? "deployment_failed" : "bundle_version_identity_conflict"),
            lifecycleRejection?.Message ?? identityConflict?.Message);
    }

    private async Task RecordFailedRollbackAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeActiveBundlePointer? active,
        string phase,
        Exception exception,
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

        var deploymentId = await InsertBundleDeploymentAsync(
            connection,
            transaction,
            active?.BundleVersionId,
            migrationPlanId: null,
            tenant.Environment.ToString().ToLowerInvariant(),
            "failed",
            SanitizeLifecycleError(exception.Message),
            new
            {
                operation = "rollback",
                phase,
                failed_step = phase,
                active_bundle_before = active is null
                    ? null
                    : new
                    {
                        active.BundleVersionId,
                        active.BundleVersion,
                        active.BundleHash
                    },
                metadata_activated = false,
                recovery_hint = "Rollback failed before active metadata was changed; the previously active bundle remains authoritative."
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        logger?.LogWarning(
            "Failed runtime rollback metadata recorded for tenant {TenantId} environment {Environment}; deployment {DeploymentId}; active bundle version id {ActiveBundleVersionId}; phase {Phase}.",
            tenant.TenantStableId,
            tenant.Environment.ToString().ToLowerInvariant(),
            deploymentId,
            active?.BundleVersionId,
            phase);
    }

    private static async Task EnsureDeploymentLifecycleTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var statement in DeploymentLifecycleStatements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static IReadOnlyList<string> DeploymentLifecycleStatements { get; } =
    [
        "create schema if not exists _runtime",
        """
        create table if not exists _runtime.bundle_versions (
          id bigserial primary key,
          bundle_id varchar(120) not null,
          bundle_name varchar(300) not null,
          version varchar(80) not null,
          bundle_hash varchar(128) not null,
          status varchar(40) not null default 'compiled',
          artifact_json jsonb,
          compiled_metadata jsonb,
          metadata jsonb not null,
          created_by text,
          created_at timestamptz not null default now(),
          staged_at timestamptz,
          production_at timestamptz,
          superseded_at timestamptz,
          rolled_back_at timestamptz,
          deployed_at timestamptz not null default now(),
          unique (bundle_id, version)
        )
        """,
        "alter table if exists _runtime.bundle_versions add column if not exists status varchar(40) not null default 'compiled'",
        "alter table if exists _runtime.bundle_versions add column if not exists artifact_json jsonb",
        "alter table if exists _runtime.bundle_versions add column if not exists compiled_metadata jsonb",
        "alter table if exists _runtime.bundle_versions add column if not exists created_by text",
        "alter table if exists _runtime.bundle_versions add column if not exists created_at timestamptz not null default now()",
        "alter table if exists _runtime.bundle_versions add column if not exists staged_at timestamptz",
        "alter table if exists _runtime.bundle_versions add column if not exists production_at timestamptz",
        "alter table if exists _runtime.bundle_versions add column if not exists superseded_at timestamptz",
        "alter table if exists _runtime.bundle_versions add column if not exists rolled_back_at timestamptz",
        """
        create table if not exists _runtime.active_bundle (
          id boolean primary key default true,
          bundle_version_id bigint not null references _runtime.bundle_versions(id),
          previous_bundle_version_id bigint references _runtime.bundle_versions(id),
          bundle_id varchar(120) not null,
          bundle_version varchar(80) not null,
          bundle_hash varchar(128) not null,
          activated_at timestamptz not null default now(),
          constraint active_bundle_singleton check (id)
        )
        """,
        """
        create table if not exists _runtime.bundle_migration_plans (
          id bigserial primary key,
          from_bundle_version_id bigint references _runtime.bundle_versions(id),
          to_bundle_version_id bigint references _runtime.bundle_versions(id),
          status varchar(40) not null,
          risk_level varchar(40) not null,
          operations jsonb not null,
          blocked_reasons jsonb not null default '[]'::jsonb,
          created_at timestamptz not null default now(),
          applied_at timestamptz
        )
        """,
        "alter table if exists _runtime.bundle_migration_plans alter column to_bundle_version_id drop not null",
        """
        create table if not exists _runtime.bundle_deployments (
          id bigserial primary key,
          bundle_version_id bigint references _runtime.bundle_versions(id),
          migration_plan_id bigint references _runtime.bundle_migration_plans(id),
          environment varchar(40) not null,
          status varchar(40) not null,
          started_at timestamptz not null default now(),
          finished_at timestamptz,
          error_message text,
          metadata jsonb not null default '{}'::jsonb
        )
        """
    ];

    private static string SanitizeLifecycleError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Deployment lifecycle operation failed.";
        }

        var sanitized = message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        if (sanitized.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
            (sanitized.Contains("Host=", StringComparison.OrdinalIgnoreCase) &&
             sanitized.Contains("Username=", StringComparison.OrdinalIgnoreCase)))
        {
            return "Deployment lifecycle operation failed. See runtime logs for details.";
        }

        return sanitized.Length <= 1000 ? sanitized : sanitized[..1000];
    }
}
