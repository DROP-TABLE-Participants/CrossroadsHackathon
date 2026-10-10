using System.Text.Json;
using Npgsql;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed partial class RuntimeDatabase
{
    private async Task<CompiledBundle?> ReadActiveCompiledBundleUnderLockAsync(
        ResolvedTenantEnvironment tenant,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(
            connectionFactory.CreateConnectionString(tenant, TenantDatabaseConnectionPurpose.Provisioning));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var active = await ReadActiveCompiledBundleAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return active;
    }

    private void EnsureErpActivationAuthorized(
        ResolvedTenantEnvironment tenant,
        CompiledBundle? active,
        CompiledBundle candidate,
        RuntimeLifecyclePrivilegePromotionAuthorization? authorization)
    {
        if (active is null) return; // Initial ERP creation has no existing privilege surface to widen.
        var targetIdentity = RuntimeLifecyclePrivilegePromotionAuthorization.ComputeEnvironmentIdentity(
            tenant.TenantStableId,
            TenantEnvironmentKind.Erp.ToString(),
            tenant.DatabaseName);
        var result = productionActivationPolicy.Validate(
            active,
            candidate,
            authorization,
            tenant.TenantStableId,
            targetIdentity);
        if (!result.IsValid)
        {
            throw new RuntimeLifecyclePrivilegePromotionRejectedException(result);
        }
    }

    private async Task ConsumeLifecyclePrivilegeEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompiledBundle? active,
        CompiledBundle candidate,
        RuntimeLifecyclePrivilegePromotionAuthorization? authorization,
        CancellationToken cancellationToken)
    {
        if (active is null) return;
        var changes = RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate);
        if (changes.Count == 0)
        {
            return;
        }

        var machine = authorization?.MachineAttestation;
        var supervisor = authorization?.SupervisorAttestation;
        if (machine is null || supervisor is null)
        {
            throw new RuntimeLifecyclePrivilegePromotionRejectedException(
                RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                    "lifecycle_attestation_missing",
                    "Production lifecycle privilege widening requires exact machine and supervisor attestations."));
        }

        await EnsureLifecyclePrivilegeConsumptionTableAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into _runtime.lifecycle_privilege_attestation_consumptions
              (attestation_id, review_id, attestation_hash, review_hash, operation_id, run_id,
               generation_project_id, tenant_stable_id, source_environment_identity,
               production_environment_identity, test_suite_hash, apply_identity,
               base_bundle_hash, candidate_bundle_hash, change_digest)
            values
              (@attestationId, @reviewId, @attestationHash, @reviewHash, @operationId, @runId,
               @generationProjectId, @tenantStableId, @sourceEnvironmentIdentity,
               @productionEnvironmentIdentity, @testSuiteHash, @applyIdentity,
               @baseBundleHash, @candidateBundleHash, @changeDigest)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("attestationId", machine.AttestationId);
        command.Parameters.AddWithValue("reviewId", supervisor.ReviewId);
        command.Parameters.AddWithValue("attestationHash", machine.AttestationHash);
        command.Parameters.AddWithValue("reviewHash", supervisor.ReviewHash);
        command.Parameters.AddWithValue("operationId", authorization!.OperationId);
        command.Parameters.AddWithValue("runId", authorization.RunId);
        command.Parameters.AddWithValue("generationProjectId", authorization.GenerationProjectId);
        command.Parameters.AddWithValue("tenantStableId", authorization.TenantStableId);
        command.Parameters.AddWithValue("sourceEnvironmentIdentity", authorization.SourceEnvironmentIdentity);
        command.Parameters.AddWithValue("productionEnvironmentIdentity", authorization.ProductionEnvironmentIdentity);
        command.Parameters.AddWithValue("testSuiteHash", authorization.TestSuiteHash);
        command.Parameters.AddWithValue("applyIdentity", authorization.ApplyIdentity);
        command.Parameters.AddWithValue("baseBundleHash", authorization.BaseBundleHash);
        command.Parameters.AddWithValue("candidateBundleHash", authorization.CandidateBundleHash);
        command.Parameters.AddWithValue(
            "changeDigest",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(changes)))
                .ToLowerInvariant());
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RuntimeLifecyclePrivilegePromotionRejectedException(
                RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                    "lifecycle_attestation_replayed",
                    "Lifecycle privilege attestation or supervisor review evidence has already been consumed."));
        }
    }

    private static async Task<CompiledBundle?> ReadActiveCompiledBundleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await RuntimeTableExistsAsync(connection, transaction, "_runtime.active_bundle", cancellationToken))
        {
            return null;
        }

        await using var command = new NpgsqlCommand(
            """
            select bv.metadata
            from _runtime.active_bundle ab
            join _runtime.bundle_versions bv on bv.id = ab.bundle_version_id
            where ab.id = true
            limit 1
            """,
            connection,
            transaction);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<CompiledBundle>(json, JsonOptions);
    }

    private static async Task<bool> RuntimeTableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select to_regclass(@table) is not null", connection, transaction);
        command.Parameters.AddWithValue("table", table);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task EnsureLifecyclePrivilegeConsumptionTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var schema = new NpgsqlCommand("create schema if not exists _runtime", connection, transaction))
        {
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            """
            create table if not exists _runtime.lifecycle_privilege_attestation_consumptions (
              attestation_id text primary key,
              review_id text not null unique,
              attestation_hash varchar(64) not null unique,
              review_hash varchar(64) not null unique,
              operation_id text not null,
              run_id text not null,
              generation_project_id text not null,
              tenant_stable_id text not null,
              source_environment_identity varchar(64) not null,
              production_environment_identity varchar(64) not null,
              test_suite_hash varchar(64) not null,
              apply_identity text not null,
              base_bundle_hash varchar(64) not null,
              candidate_bundle_hash varchar(64) not null,
              change_digest varchar(64) not null,
              consumed_at timestamptz not null default now()
            )
            """,
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
