using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Shared.Deployment;

public enum RuntimeLifecyclePrivilegeMachineStatus
{
    Passed,
    Failed
}

public enum RuntimeLifecyclePrivilegeSupervisorDecision
{
    Approve,
    Reject
}

public sealed record RuntimeLifecyclePrivilegeChange(
    string Entity,
    string Field,
    string? BaseInitial,
    string? CandidateInitial,
    string? BaseTransitions,
    string? CandidateTransitions,
    string Kind = "field_lifecycle",
    string? Authority = null);

public sealed record RuntimeLifecyclePrivilegeMachineAttestation(
    string AttestationId,
    string PolicyVersion,
    string Producer,
    string OperationId,
    string RunId,
    string GenerationProjectId,
    string TenantStableId,
    string SourceEnvironmentIdentity,
    string ProductionEnvironmentIdentity,
    string TestSuiteHash,
    string ApplyIdentity,
    string BaseBundleHash,
    string CandidateBundleHash,
    RuntimeLifecyclePrivilegeMachineStatus Status,
    IReadOnlyList<RuntimeLifecyclePrivilegeChange> Changes,
    DateTimeOffset AttestedAt,
    string AttestationHash,
    string ControllerReceipt);

public sealed record RuntimeLifecyclePrivilegeSupervisorAttestation(
    string ReviewId,
    string PolicyVersion,
    string Reviewer,
    string OperationId,
    string RunId,
    string GenerationProjectId,
    string TenantStableId,
    string SourceEnvironmentIdentity,
    string ProductionEnvironmentIdentity,
    string TestSuiteHash,
    string ApplyIdentity,
    string BaseBundleHash,
    string CandidateBundleHash,
    string MachineAttestationHash,
    string MeasuredReviewHash,
    RuntimeLifecyclePrivilegeSupervisorDecision Decision,
    DateTimeOffset ReviewedAt,
    string ReviewHash,
    string ControllerReceipt);

public sealed record RuntimeLifecyclePrivilegePromotionAuthorization(
    string OperationId,
    string RunId,
    string GenerationProjectId,
    string TenantStableId,
    string SourceEnvironmentIdentity,
    string ProductionEnvironmentIdentity,
    string TestSuiteHash,
    string ApplyIdentity,
    string BaseBundleHash,
    string CandidateBundleHash,
    RuntimeLifecyclePrivilegeMachineAttestation? MachineAttestation = null,
    RuntimeLifecyclePrivilegeSupervisorAttestation? SupervisorAttestation = null)
{
    public static RuntimeLifecyclePrivilegePromotionAuthorization FromBrokerRequest(
        RuntimeProductionPromotionBrokerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RuntimeLifecyclePrivilegePromotionAuthorization(
            request.OperationId,
            request.RunId,
            request.GenerationProjectId,
            request.ProductionTarget.TenantStableId,
            ComputeEnvironmentIdentity(request.StagingTarget),
            ComputeEnvironmentIdentity(request.ProductionTarget),
            request.TestSuiteHash,
            request.LifecyclePrivilegeApplyIdentity ?? string.Empty,
            request.ExpectedBaseBundleHash ?? string.Empty,
            request.BundleHash,
            request.LifecyclePrivilegeMachineAttestation,
            request.LifecyclePrivilegeSupervisorAttestation);
    }

    public static string ComputeEnvironmentIdentity(RuntimeDeploymentTarget target) =>
        ComputeEnvironmentIdentity(target.TenantStableId, target.Environment, target.DatabaseName);

    public static string ComputeEnvironmentIdentity(
        string tenantStableId,
        string environment,
        string databaseName)
    {
        var canonical = string.Join(
            '\n',
            RequiredIdentityPart(tenantStableId, nameof(tenantStableId)),
            RequiredIdentityPart(environment, nameof(environment)).ToLowerInvariant(),
            RequiredIdentityPart(databaseName, nameof(databaseName)).ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string ComputeProposalApplyIdentity(string proposalId, string applyRequestId)
    {
        var proposal = RequiredIdentityPart(proposalId, nameof(proposalId));
        var apply = RequiredIdentityPart(applyRequestId, nameof(applyRequestId));
        var canonical = $"{Encoding.UTF8.GetByteCount(proposal)}:{proposal}{Encoding.UTF8.GetByteCount(apply)}:{apply}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string RequiredIdentityPart(string? value, string name)
    {
        var result = value?.Trim();
        return string.IsNullOrWhiteSpace(result) || result.Length > 256 || result.Any(char.IsControl)
            ? throw new ArgumentException($"{name} is required and must be bounded safe text.", name)
            : result;
    }
}

public sealed record RuntimeLifecyclePrivilegeAttestationValidationResult(
    bool IsValid,
    string? FailureCode = null,
    string? SafeError = null)
{
    public static RuntimeLifecyclePrivilegeAttestationValidationResult Valid { get; } = new(true);

    public static RuntimeLifecyclePrivilegeAttestationValidationResult Invalid(string code, string error) =>
        new(false, code, error);
}

public sealed class RuntimeLifecyclePrivilegePromotionRejectedException(
    RuntimeLifecyclePrivilegeAttestationValidationResult rejection)
    : InvalidOperationException(rejection.SafeError ?? "Production lifecycle privilege promotion was rejected.")
{
    public string FailureCode { get; } = rejection.FailureCode ?? "lifecycle_attestation_invalid";
}

public static class RuntimeLifecyclePrivilegePromotionRules
{
    public const string NamedAction = "named_action";
    public const string Direct = "direct";
    public static string NoActiveProductionBundleHash { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("erpgenerator:no-active-production-bundle:v1")))
            .ToLowerInvariant();

    public static IReadOnlyList<RuntimeLifecyclePrivilegeChange> FindWidening(
        CompiledBundle? activeProduction,
        CompiledBundle stagedCandidate)
    {
        ArgumentNullException.ThrowIfNull(stagedCandidate);
        var activeEntities = activeProduction?.Entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, EntityDefinition>(StringComparer.OrdinalIgnoreCase);
        var candidateEntities = stagedCandidate.Entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        var changes = new List<RuntimeLifecyclePrivilegeChange>();

        foreach (var candidateEntity in stagedCandidate.Entities)
        {
            activeEntities.TryGetValue(candidateEntity.Id, out var activeEntity);
            var activeFields = activeEntity?.Fields.ToDictionary(field => field.Id, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidateField in candidateEntity.Fields)
            {
                if (!activeFields.TryGetValue(candidateField.Id, out var activeField))
                {
                    if (string.Equals(candidateField.Lifecycle?.Transitions, Direct, StringComparison.OrdinalIgnoreCase))
                    {
                        changes.Add(Change(
                            candidateEntity.Id,
                            candidateField.Id,
                            null,
                            candidateField.Lifecycle,
                            "candidate_direct_lifecycle"));
                    }

                    if (candidateField.Lifecycle is not null)
                    {
                        foreach (var authority in EffectiveAuthorityFacts(stagedCandidate, candidateEntity, candidateField))
                        {
                            var separator = authority.IndexOf(':');
                            var kind = separator > 0 ? authority[..separator] : "lifecycle_authority";
                            changes.Add(Change(
                                candidateEntity.Id,
                                candidateField.Id,
                                null,
                                candidateField.Lifecycle,
                                kind,
                                Hash(authority)));
                        }
                    }

                    continue;
                }

                var initialChanged = !string.Equals(
                    activeField.Lifecycle?.Initial,
                    candidateField.Lifecycle?.Initial,
                    StringComparison.Ordinal);
                var namedActionWidened = string.Equals(
                        activeField.Lifecycle?.Transitions,
                        NamedAction,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(
                        candidateField.Lifecycle?.Transitions,
                        NamedAction,
                        StringComparison.OrdinalIgnoreCase);
                if (initialChanged || namedActionWidened)
                {
                    changes.Add(Change(
                        candidateEntity.Id,
                        candidateField.Id,
                        activeField.Lifecycle,
                        candidateField.Lifecycle,
                        "field_lifecycle"));
                }

                if (candidateField.Lifecycle is null)
                {
                    continue;
                }

                var activeAuthority = activeField.Lifecycle is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : EffectiveAuthorityFacts(activeProduction!, activeEntity!, activeField);
                var candidateAuthority = EffectiveAuthorityFacts(
                    stagedCandidate,
                    candidateEntity,
                    candidateField);
                foreach (var authority in candidateAuthority.Except(activeAuthority, StringComparer.Ordinal))
                {
                    var separator = authority.IndexOf(':');
                    var kind = separator > 0 ? authority[..separator] : "lifecycle_authority";
                    changes.Add(Change(
                        candidateEntity.Id,
                        candidateField.Id,
                        activeField.Lifecycle,
                        candidateField.Lifecycle,
                        kind,
                        Hash(authority)));
                }
            }
        }

        return changes
            .OrderBy(change => change.Entity, StringComparer.Ordinal)
            .ThenBy(change => change.Field, StringComparer.Ordinal)
            .ThenBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.Authority, StringComparer.Ordinal)
            .ToArray();
    }

    private static RuntimeLifecyclePrivilegeChange Change(
        string entity,
        string field,
        FieldLifecycleDefinition? active,
        FieldLifecycleDefinition? candidate,
        string kind,
        string? authority = null) =>
        new(
            entity,
            field,
            active?.Initial,
            candidate?.Initial,
            active?.Transitions,
            candidate?.Transitions,
            kind,
            authority);

    private static IReadOnlySet<string> EffectiveAuthorityFacts(
        CompiledBundle bundle,
        EntityDefinition entity,
        FieldDefinition field)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.Equals(field.Lifecycle?.Transitions, Direct, StringComparison.OrdinalIgnoreCase))
        {
            result.Add(DirectAuthorityFact(
                bundle,
                entity,
                field,
                RuntimeSystemRoleIds.TenantOwner,
                []));
            foreach (var role in bundle.Roles)
            {
                if (HasDirectAuthority([role], entity, field))
                {
                    result.Add(DirectAuthorityFact(bundle, entity, field, role.Id, [role]));
                }
            }

            if (bundle.Roles.Count > 1 && HasDirectAuthority(bundle.Roles, entity, field))
            {
                result.Add(DirectAuthorityFact(bundle, entity, field, "bundle_role_set", bundle.Roles));
            }

            return result;
        }

        if (!string.Equals(field.Lifecycle?.Transitions, NamedAction, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        var mutations = LifecycleActionMutations(bundle, entity.Id, field.Id);
        foreach (var mutation in mutations)
        {
            result.Add(NamedActionAuthorityFact(
                bundle,
                entity,
                field,
                mutation,
                RuntimeSystemRoleIds.TenantOwner,
                []));
            foreach (var role in bundle.Roles)
            {
                if (HasNamedActionAuthority(bundle, [role], entity, field, mutation.Action))
                {
                    result.Add(NamedActionAuthorityFact(
                        bundle,
                        entity,
                        field,
                        mutation,
                        role.Id,
                        [role]));
                }
            }

            if (bundle.Roles.Count > 1 &&
                HasNamedActionAuthority(bundle, bundle.Roles, entity, field, mutation.Action))
            {
                result.Add(NamedActionAuthorityFact(
                    bundle,
                    entity,
                    field,
                    mutation,
                    "bundle_role_set",
                    bundle.Roles));
            }
        }

        return result;
    }

    private static IReadOnlyList<LifecycleActionMutation> LifecycleActionMutations(
        CompiledBundle bundle,
        string entityId,
        string fieldId)
    {
        var targetEntity = ResolveEntity(bundle, entityId);
        var targetField = targetEntity?.Fields.FirstOrDefault(candidate => FieldMatches(candidate, fieldId));
        if (targetEntity is null || targetField is null)
        {
            return [];
        }

        var result = new List<LifecycleActionMutation>();
        foreach (var action in bundle.Actions)
        {
            var sourceEntity = ResolveEntity(bundle, action.Entity);
            if (sourceEntity is null ||
                !action.Permissions.Any(permission =>
                    permission.Action.Equals(
                        ActionPermissionEffectAnalyzer.EffectUpdate,
                        StringComparison.OrdinalIgnoreCase) &&
                    EntityMatches(permission.Entity, targetEntity, allowWildcard: false)))
            {
                continue;
            }

            var queryEntities = new Dictionary<string, EntityDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in action.Steps)
            {
                if (!step.Type.Equals("query_records", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(step.Entity) ||
                    ResolveEntity(bundle, step.Entity) is not { } queryEntity)
                {
                    continue;
                }

                queryEntities.TryAdd(step.Id, queryEntity);
            }

            var mutatesLifecycleField = action.Steps.Any(step =>
            {
                var mutationEntity = step.Type.Trim().ToLowerInvariant() switch
                {
                    "update_current_record" => sourceEntity,
                    "update_records" when step.RecordsFrom is not null &&
                                          queryEntities.TryGetValue(step.RecordsFrom.Result, out var queryEntity) => queryEntity,
                    _ => null
                };
                if (mutationEntity is null ||
                    !mutationEntity.Id.Equals(targetEntity.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return step.Set.Keys.Any(key => FieldMatches(targetField, key));
            });

            if (mutatesLifecycleField)
            {
                result.Add(new LifecycleActionMutation(action, sourceEntity));
            }
        }

        return result;
    }

    private static bool HasDirectAuthority(
        IReadOnlyList<RoleDefinition> roles,
        EntityDefinition entity,
        FieldDefinition field) =>
        HasRuntimePermission(roles, "update", entity) &&
        HasRuntimeFieldPermission(roles, GranularPermissionActions.Write, entity, field);

    private static bool HasNamedActionAuthority(
        CompiledBundle bundle,
        IReadOnlyList<RoleDefinition> roles,
        EntityDefinition targetEntity,
        FieldDefinition targetField,
        ActionDefinition action)
    {
        var sourceEntity = ResolveEntity(bundle, action.Entity);
        return sourceEntity is not null &&
               HasRuntimePermission(roles, "read", sourceEntity) &&
               HasNamedActionGrant(roles, sourceEntity, action.Key) &&
               action.Permissions.All(requirement =>
                   ResolveEntity(bundle, requirement.Entity) is { } requiredEntity &&
                   HasRuntimePermission(roles, requirement.Action, requiredEntity)) &&
               HasRuntimePermission(
                   roles,
                   ActionPermissionEffectAnalyzer.EffectUpdate,
                   targetEntity) &&
               HasRuntimeFieldPermission(
                   roles,
                   GranularPermissionActions.EffectWrite,
                   targetEntity,
                   targetField);
    }

    private static bool HasRuntimePermission(
        IReadOnlyList<RoleDefinition> roles,
        string action,
        EntityDefinition entity)
    {
        var normalizedAction = action.Trim().ToLowerInvariant();
        if (normalizedAction is ActionPermissionEffectAnalyzer.EffectCreate or
            ActionPermissionEffectAnalyzer.EffectUpdate)
        {
            return roles.SelectMany(role => role.Grants).Any(grant =>
                grant.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase) &&
                grant.Action.Equals(normalizedAction, StringComparison.OrdinalIgnoreCase) &&
                EntityMatches(grant.Entity, entity, allowWildcard: false, effectIdentity: true));
        }

        return roles.SelectMany(role => role.Grants).Any(grant =>
            (grant.Scope.Equals(PermissionGrantScopes.Entity, StringComparison.OrdinalIgnoreCase) ||
             grant.Scope.Equals(PermissionGrantScopes.Record, StringComparison.OrdinalIgnoreCase)) &&
            ActionPermissionEffectAnalyzer.ActionMatches(grant.Action, normalizedAction) &&
            EntityMatches(grant.Entity, entity, allowWildcard: true));
    }

    private static bool HasRuntimeFieldPermission(
        IReadOnlyList<RoleDefinition> roles,
        string action,
        EntityDefinition entity,
        FieldDefinition field)
    {
        var matching = roles
            .SelectMany(role => role.Grants)
            .Where(grant =>
                grant.Scope.Equals(PermissionGrantScopes.Field, StringComparison.OrdinalIgnoreCase) &&
                ActionPermissionEffectAnalyzer.ActionMatches(grant.Action, action) &&
                EntityMatches(grant.Entity, entity, allowWildcard: true))
            .ToArray();
        var requiresExplicitEffectField = action.Equals(
            GranularPermissionActions.EffectWrite,
            StringComparison.OrdinalIgnoreCase);
        return (!requiresExplicitEffectField && matching.Length == 0) ||
               matching.Any(grant => FieldMatches(field, grant.Target));
    }

    private static bool HasNamedActionGrant(
        IReadOnlyList<RoleDefinition> roles,
        EntityDefinition sourceEntity,
        string actionKey) =>
        roles.SelectMany(role => role.Grants).Any(grant =>
            grant.Scope.Equals(PermissionGrantScopes.Action, StringComparison.OrdinalIgnoreCase) &&
            ActionPermissionEffectAnalyzer.ActionMatches(grant.Action, GranularPermissionActions.Execute) &&
            EntityMatches(grant.Entity, sourceEntity, allowWildcard: true) &&
            grant.Target.Equals(actionKey, StringComparison.OrdinalIgnoreCase));

    private static string DirectAuthorityFact(
        CompiledBundle bundle,
        EntityDefinition entity,
        FieldDefinition field,
        string principal,
        IReadOnlyList<RoleDefinition> roles)
    {
        var digest = CanonicalHash(new
        {
            Principal = principal,
            Roles = roles.OrderBy(role => role.Id, StringComparer.Ordinal).ToArray(),
            Field = field,
            Entities = RelevantEntitySchemas(bundle, entity, action: null, roles)
        });
        return $"direct_authority:{principal}:{entity.Id}:{field.Id}:{digest}";
    }

    private static string NamedActionAuthorityFact(
        CompiledBundle bundle,
        EntityDefinition entity,
        FieldDefinition field,
        LifecycleActionMutation mutation,
        string principal,
        IReadOnlyList<RoleDefinition> roles)
    {
        var digest = CanonicalHash(new
        {
            Principal = principal,
            Roles = roles.OrderBy(role => role.Id, StringComparer.Ordinal).ToArray(),
            Action = mutation.Action,
            Field = field,
            Entities = RelevantEntitySchemas(bundle, entity, mutation.Action, roles)
        });
        return $"named_action_authority:{principal}:{mutation.SourceEntity.Id}:{mutation.Action.Key}:{digest}";
    }

    private static IReadOnlyList<EntityDefinition> RelevantEntitySchemas(
        CompiledBundle bundle,
        EntityDefinition lifecycleEntity,
        ActionDefinition? action,
        IReadOnlyList<RoleDefinition> roles)
    {
        var entityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lifecycleEntity.Id };
        AddEntityReference(bundle, entityIds, action?.Entity);
        if (action is not null)
        {
            foreach (var permission in action.Permissions)
            {
                AddEntityReference(bundle, entityIds, permission.Entity);
            }

            foreach (var step in action.Steps)
            {
                AddEntityReference(bundle, entityIds, step.Entity);
                AddEntityReference(bundle, entityIds, step.Return?.Entity);
            }
        }

        foreach (var grant in roles.SelectMany(role => role.Grants))
        {
            AddEntityReference(bundle, entityIds, grant.Entity);
            AddEntityReference(bundle, entityIds, grant.RecordScope?.LinkedEntity);
        }

        var addedReference = true;
        while (addedReference)
        {
            addedReference = false;
            foreach (var reference in bundle.Entities
                         .Where(candidate => entityIds.Contains(candidate.Id))
                         .SelectMany(candidate => candidate.Fields)
                         .Select(candidate => candidate.References)
                         .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                         .ToArray())
            {
                var before = entityIds.Count;
                AddEntityReference(bundle, entityIds, reference);
                addedReference |= entityIds.Count != before;
            }
        }

        return bundle.Entities
            .Where(candidate => entityIds.Contains(candidate.Id))
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddEntityReference(
        CompiledBundle bundle,
        HashSet<string> entityIds,
        string? entityReference)
    {
        if (string.IsNullOrWhiteSpace(entityReference))
        {
            return;
        }

        if (entityReference.Trim().Equals("*", StringComparison.Ordinal))
        {
            entityIds.UnionWith(bundle.Entities.Select(entity => entity.Id));
            return;
        }

        if (ResolveEntity(bundle, entityReference) is { } entity)
        {
            entityIds.Add(entity.Id);
        }
    }

    private static EntityDefinition? ResolveEntity(CompiledBundle bundle, string? entityReference)
    {
        if (string.IsNullOrWhiteSpace(entityReference))
        {
            return null;
        }

        var normalized = entityReference.Trim();
        var matches = bundle.Entities.Where(entity =>
                entity.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                entity.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                entity.TableName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool EntityMatches(
        string? reference,
        EntityDefinition entity,
        bool allowWildcard,
        bool effectIdentity = false)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var normalized = reference.Trim();
        return allowWildcard && normalized.Equals("*", StringComparison.Ordinal) ||
               entity.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
               entity.TableName.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
               !effectIdentity && entity.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FieldMatches(FieldDefinition field, string? reference) =>
        !string.IsNullOrWhiteSpace(reference) &&
        (field.Id.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase) ||
         field.Name.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase) ||
         field.ColumnName.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string CanonicalHash(object value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            BundleAcceptanceCanonicalJson.WriteValue(writer, value);
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record LifecycleActionMutation(ActionDefinition Action, EntityDefinition SourceEntity);
}

/// <summary>
/// Mandatory policy used by every production activation path. It recomputes the
/// privilege diff from the actual active and candidate bundles; callers can only
/// transport evidence, never declare the diff that is authorized.
/// </summary>
public sealed class RuntimeLifecycleProductionActivationPolicy(
    string? machineVerificationSecret,
    string? supervisorVerificationSecret,
    TimeProvider? timeProvider = null)
{
    public RuntimeLifecyclePrivilegeAttestationValidationResult Validate(
        CompiledBundle? activeProduction,
        CompiledBundle candidate,
        RuntimeLifecyclePrivilegePromotionAuthorization? authorization,
        string? expectedTenantStableId = null,
        string? expectedProductionEnvironmentIdentity = null)
    {
        var changes = RuntimeLifecyclePrivilegePromotionRules.FindWidening(activeProduction, candidate);
        if (changes.Count == 0)
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Valid;
        }

        if (authorization is null)
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                "lifecycle_attestation_missing",
                "Production lifecycle privilege widening requires exact machine and supervisor attestations.");
        }

        var actualBaseHash = activeProduction?.BundleHash ?? RuntimeLifecyclePrivilegePromotionRules.NoActiveProductionBundleHash;
        if (!string.Equals(actualBaseHash, authorization.BaseBundleHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.BundleHash, authorization.CandidateBundleHash, StringComparison.OrdinalIgnoreCase) ||
            expectedTenantStableId is not null &&
            !string.Equals(expectedTenantStableId, authorization.TenantStableId, StringComparison.Ordinal) ||
            expectedProductionEnvironmentIdentity is not null &&
            !string.Equals(
                expectedProductionEnvironmentIdentity,
                authorization.ProductionEnvironmentIdentity,
                StringComparison.Ordinal))
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                "lifecycle_attestation_lineage_mismatch",
                "Lifecycle privilege attestations do not match the actual production target and exact active/candidate bundle hashes.");
        }

        try
        {
            using var authority = new RuntimeLifecyclePrivilegeAttestationAuthority(
                machineVerificationSecret ?? string.Empty,
                supervisorVerificationSecret ?? string.Empty,
                timeProvider);
            return authority.Validate(authorization, changes);
        }
        catch (ArgumentException)
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                "lifecycle_attestation_unavailable",
                "Lifecycle privilege attestation verification is unavailable; production promotion is blocked.");
        }
    }
}

/// <summary>
/// Separate machine and supervisor HMAC verifier identities. Signing helpers are
/// internal and exist only for autonomous issuer integration tests; there is no
/// product or operator signing surface.
/// </summary>
public sealed class RuntimeLifecyclePrivilegeAttestationAuthority : IDisposable
{
    public const string CurrentPolicyVersion = "runtime-lifecycle-privilege-promotion-v3";
    public const string MachineProducer = "lifecycle_privilege_diff_engine";
    public const string SupervisorReviewer = "lifecycle_privilege_supervisor";
    public const int MaximumAttestedChanges = 4096;

    private const string MachineDomain = "erpgenerator.runtime-lifecycle.machine.v3";
    private const string SupervisorDomain = "erpgenerator.runtime-lifecycle.supervisor.v3";
    private static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly byte[] machineSecret;
    private readonly byte[] supervisorSecret;
    private readonly TimeProvider timeProvider;
    private bool disposed;

    public RuntimeLifecyclePrivilegeAttestationAuthority(
        string machineVerificationSecret,
        string supervisorVerificationSecret,
        TimeProvider? timeProvider = null)
    {
        machineSecret = ValidateSecret(machineVerificationSecret, nameof(machineVerificationSecret));
        supervisorSecret = ValidateSecret(supervisorVerificationSecret, nameof(supervisorVerificationSecret));
        if (SHA256.HashData(machineSecret).SequenceEqual(SHA256.HashData(supervisorSecret)))
        {
            CryptographicOperations.ZeroMemory(machineSecret);
            CryptographicOperations.ZeroMemory(supervisorSecret);
            throw new ArgumentException("Machine and supervisor lifecycle verification secrets must be distinct.");
        }

        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal RuntimeLifecyclePrivilegeMachineAttestation RecordMachineAttestation(
        string attestationId,
        RuntimeLifecyclePrivilegePromotionAuthorization authorization,
        RuntimeLifecyclePrivilegeMachineStatus status,
        IEnumerable<RuntimeLifecyclePrivilegeChange> changes,
        DateTimeOffset? attestedAt = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(authorization);
        var normalizedChanges = NormalizeChanges(changes);
        var timestamp = attestedAt ?? timeProvider.GetUtcNow();
        var hash = MachineHash(attestationId, authorization, status, normalizedChanges, timestamp);
        return new RuntimeLifecyclePrivilegeMachineAttestation(
            Required(attestationId, nameof(attestationId)),
            CurrentPolicyVersion,
            MachineProducer,
            Required(authorization.OperationId, nameof(authorization.OperationId)),
            Required(authorization.RunId, nameof(authorization.RunId)),
            Required(authorization.GenerationProjectId, nameof(authorization.GenerationProjectId)),
            Required(authorization.TenantStableId, nameof(authorization.TenantStableId)),
            RequiredHash(authorization.SourceEnvironmentIdentity, nameof(authorization.SourceEnvironmentIdentity)),
            RequiredHash(authorization.ProductionEnvironmentIdentity, nameof(authorization.ProductionEnvironmentIdentity)),
            RequiredHash(authorization.TestSuiteHash, nameof(authorization.TestSuiteHash)),
            Required(authorization.ApplyIdentity, nameof(authorization.ApplyIdentity)),
            RequiredHash(authorization.BaseBundleHash, nameof(authorization.BaseBundleHash)),
            RequiredHash(authorization.CandidateBundleHash, nameof(authorization.CandidateBundleHash)),
            status,
            normalizedChanges,
            timestamp,
            hash,
            Sign(machineSecret, MachineDomain, hash));
    }

    internal RuntimeLifecyclePrivilegeSupervisorAttestation RecordSupervisorAttestation(
        string reviewId,
        RuntimeLifecyclePrivilegeMachineAttestation machine,
        string measuredReviewHash,
        RuntimeLifecyclePrivilegeSupervisorDecision decision,
        DateTimeOffset? reviewedAt = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(machine);
        var timestamp = reviewedAt ?? timeProvider.GetUtcNow();
        var hash = SupervisorHash(reviewId, machine, measuredReviewHash, decision, timestamp);
        return new RuntimeLifecyclePrivilegeSupervisorAttestation(
            Required(reviewId, nameof(reviewId)),
            CurrentPolicyVersion,
            SupervisorReviewer,
            machine.OperationId,
            machine.RunId,
            machine.GenerationProjectId,
            machine.TenantStableId,
            machine.SourceEnvironmentIdentity,
            machine.ProductionEnvironmentIdentity,
            machine.TestSuiteHash,
            machine.ApplyIdentity,
            machine.BaseBundleHash,
            machine.CandidateBundleHash,
            machine.AttestationHash,
            RequiredHash(measuredReviewHash, nameof(measuredReviewHash)),
            decision,
            timestamp,
            hash,
            Sign(supervisorSecret, SupervisorDomain, hash));
    }

    public RuntimeLifecyclePrivilegeAttestationValidationResult Validate(
        RuntimeProductionPromotionBrokerRequest request,
        IReadOnlyList<RuntimeLifecyclePrivilegeChange> computedChanges) =>
        Validate(RuntimeLifecyclePrivilegePromotionAuthorization.FromBrokerRequest(request), computedChanges);

    public RuntimeLifecyclePrivilegeAttestationValidationResult Validate(
        RuntimeLifecyclePrivilegePromotionAuthorization authorization,
        IReadOnlyList<RuntimeLifecyclePrivilegeChange> computedChanges)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(computedChanges);
        if (computedChanges.Count == 0)
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Valid;
        }

        var machine = authorization.MachineAttestation;
        var supervisor = authorization.SupervisorAttestation;
        if (machine is null || supervisor is null)
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                "lifecycle_attestation_missing",
                "Production lifecycle privilege widening requires exact machine and supervisor attestations.");
        }

        if (!Verify(machine) || !Verify(supervisor))
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                "lifecycle_attestation_invalid",
                "Production lifecycle privilege attestations are invalid.");
        }

        if (!Matches(machine.OperationId, supervisor.OperationId, authorization.OperationId) ||
            !Matches(machine.RunId, supervisor.RunId, authorization.RunId) ||
            !Matches(machine.GenerationProjectId, supervisor.GenerationProjectId, authorization.GenerationProjectId) ||
            !Matches(machine.TenantStableId, supervisor.TenantStableId, authorization.TenantStableId) ||
            !Matches(machine.SourceEnvironmentIdentity, supervisor.SourceEnvironmentIdentity, authorization.SourceEnvironmentIdentity) ||
            !Matches(machine.ProductionEnvironmentIdentity, supervisor.ProductionEnvironmentIdentity, authorization.ProductionEnvironmentIdentity) ||
            !Matches(machine.TestSuiteHash, supervisor.TestSuiteHash, authorization.TestSuiteHash) ||
            !Matches(machine.ApplyIdentity, supervisor.ApplyIdentity, authorization.ApplyIdentity) ||
            !MatchesHash(machine.BaseBundleHash, supervisor.BaseBundleHash, authorization.BaseBundleHash) ||
            !MatchesHash(machine.CandidateBundleHash, supervisor.CandidateBundleHash, authorization.CandidateBundleHash) ||
            !string.Equals(supervisor.MachineAttestationHash, machine.AttestationHash, StringComparison.Ordinal) ||
            !NormalizeChanges(machine.Changes).SequenceEqual(NormalizeChanges(computedChanges)))
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                "lifecycle_attestation_lineage_mismatch",
                "Lifecycle privilege attestations do not match the computed diff, target, test suite, apply identity, and exact base/candidate hashes.");
        }

        var now = timeProvider.GetUtcNow();
        if (machine.AttestedAt > supervisor.ReviewedAt ||
            machine.AttestedAt < now - MaximumEvidenceAge ||
            supervisor.ReviewedAt < now - MaximumEvidenceAge ||
            machine.AttestedAt > now + MaximumClockSkew ||
            supervisor.ReviewedAt > now + MaximumClockSkew)
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                "lifecycle_attestation_stale",
                "Production lifecycle privilege attestations are stale.");
        }

        if (machine.Status != RuntimeLifecyclePrivilegeMachineStatus.Passed ||
            supervisor.Decision != RuntimeLifecyclePrivilegeSupervisorDecision.Approve)
        {
            return RuntimeLifecyclePrivilegeAttestationValidationResult.Invalid(
                "lifecycle_attestation_rejected",
                "Lifecycle privilege machine and supervisor attestations did not approve production promotion.");
        }

        return RuntimeLifecyclePrivilegeAttestationValidationResult.Valid;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(machineSecret);
        CryptographicOperations.ZeroMemory(supervisorSecret);
        disposed = true;
    }

    private bool Verify(RuntimeLifecyclePrivilegeMachineAttestation value)
    {
        if (!string.Equals(value.PolicyVersion, CurrentPolicyVersion, StringComparison.Ordinal) ||
            !string.Equals(value.Producer, MachineProducer, StringComparison.Ordinal) ||
            !Enum.IsDefined(value.Status))
        {
            return false;
        }

        try
        {
            var authorization = AuthorizationFrom(value);
            var hash = MachineHash(
                value.AttestationId,
                authorization,
                value.Status,
                NormalizeChanges(value.Changes),
                value.AttestedAt);
            return FixedHashEquals(hash, value.AttestationHash) &&
                   FixedHashEquals(Sign(machineSecret, MachineDomain, hash), value.ControllerReceipt);
        }
        catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    private bool Verify(RuntimeLifecyclePrivilegeSupervisorAttestation value)
    {
        if (!string.Equals(value.PolicyVersion, CurrentPolicyVersion, StringComparison.Ordinal) ||
            !string.Equals(value.Reviewer, SupervisorReviewer, StringComparison.Ordinal) ||
            !Enum.IsDefined(value.Decision))
        {
            return false;
        }

        try
        {
            var hash = SupervisorHash(
                value.ReviewId,
                value,
                value.MeasuredReviewHash,
                value.Decision,
                value.ReviewedAt);
            return FixedHashEquals(hash, value.ReviewHash) &&
                   FixedHashEquals(Sign(supervisorSecret, SupervisorDomain, hash), value.ControllerReceipt);
        }
        catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    private static RuntimeLifecyclePrivilegePromotionAuthorization AuthorizationFrom(
        RuntimeLifecyclePrivilegeMachineAttestation value) =>
        new(
            value.OperationId,
            value.RunId,
            value.GenerationProjectId,
            value.TenantStableId,
            value.SourceEnvironmentIdentity,
            value.ProductionEnvironmentIdentity,
            value.TestSuiteHash,
            value.ApplyIdentity,
            value.BaseBundleHash,
            value.CandidateBundleHash);

    private static IReadOnlyList<RuntimeLifecyclePrivilegeChange> NormalizeChanges(
        IEnumerable<RuntimeLifecyclePrivilegeChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var result = changes
            .Select(change => change is null
                ? throw new ArgumentException("Lifecycle privilege changes must not contain null entries.", nameof(changes))
                : new RuntimeLifecyclePrivilegeChange(
                    Required(change.Entity, nameof(change.Entity)),
                    Required(change.Field, nameof(change.Field)),
                    Optional(change.BaseInitial),
                    Optional(change.CandidateInitial),
                    Optional(change.BaseTransitions),
                    Optional(change.CandidateTransitions),
                    Required(change.Kind, nameof(change.Kind)),
                    OptionalHash(change.Authority, nameof(change.Authority))))
            .OrderBy(change => change.Entity, StringComparer.Ordinal)
            .ThenBy(change => change.Field, StringComparer.Ordinal)
            .ThenBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.Authority, StringComparer.Ordinal)
            .ToArray();
        var unique = result
            .Select(change => $"{change.Entity}:{change.Field}:{change.Kind}:{change.Authority}")
            .Distinct(StringComparer.Ordinal)
            .Count();
        // 4096 is a documented hard bound below the broker's 12 MiB request cap;
        // it supports large generated ERPs while keeping deterministic verification bounded.
        if (result.Length is < 1 or > MaximumAttestedChanges || unique != result.Length)
        {
            throw new ArgumentException(
                $"Lifecycle privilege changes must be nonempty, unique, and limited to {MaximumAttestedChanges} entries.",
                nameof(changes));
        }

        return result;
    }

    private static string MachineHash(
        string attestationId,
        RuntimeLifecyclePrivilegePromotionAuthorization authorization,
        RuntimeLifecyclePrivilegeMachineStatus status,
        IReadOnlyList<RuntimeLifecyclePrivilegeChange> changes,
        DateTimeOffset attestedAt) =>
        Hash(new
        {
            attestationId = Required(attestationId, nameof(attestationId)),
            policyVersion = CurrentPolicyVersion,
            producer = MachineProducer,
            operationId = Required(authorization.OperationId, nameof(authorization.OperationId)),
            runId = Required(authorization.RunId, nameof(authorization.RunId)),
            generationProjectId = Required(authorization.GenerationProjectId, nameof(authorization.GenerationProjectId)),
            tenantStableId = Required(authorization.TenantStableId, nameof(authorization.TenantStableId)),
            sourceEnvironmentIdentity = RequiredHash(authorization.SourceEnvironmentIdentity, nameof(authorization.SourceEnvironmentIdentity)),
            productionEnvironmentIdentity = RequiredHash(authorization.ProductionEnvironmentIdentity, nameof(authorization.ProductionEnvironmentIdentity)),
            testSuiteHash = RequiredHash(authorization.TestSuiteHash, nameof(authorization.TestSuiteHash)),
            applyIdentity = Required(authorization.ApplyIdentity, nameof(authorization.ApplyIdentity)),
            baseBundleHash = RequiredHash(authorization.BaseBundleHash, nameof(authorization.BaseBundleHash)),
            candidateBundleHash = RequiredHash(authorization.CandidateBundleHash, nameof(authorization.CandidateBundleHash)),
            status,
            changes,
            attestedAt
        });

    private static string SupervisorHash(
        string reviewId,
        RuntimeLifecyclePrivilegeMachineAttestation machine,
        string measuredReviewHash,
        RuntimeLifecyclePrivilegeSupervisorDecision decision,
        DateTimeOffset reviewedAt) =>
        SupervisorHash(
            reviewId,
            machine.OperationId,
            machine.RunId,
            machine.GenerationProjectId,
            machine.TenantStableId,
            machine.SourceEnvironmentIdentity,
            machine.ProductionEnvironmentIdentity,
            machine.TestSuiteHash,
            machine.ApplyIdentity,
            machine.BaseBundleHash,
            machine.CandidateBundleHash,
            machine.AttestationHash,
            measuredReviewHash,
            decision,
            reviewedAt);

    private static string SupervisorHash(
        string reviewId,
        RuntimeLifecyclePrivilegeSupervisorAttestation supervisor,
        string measuredReviewHash,
        RuntimeLifecyclePrivilegeSupervisorDecision decision,
        DateTimeOffset reviewedAt) =>
        SupervisorHash(
            reviewId,
            supervisor.OperationId,
            supervisor.RunId,
            supervisor.GenerationProjectId,
            supervisor.TenantStableId,
            supervisor.SourceEnvironmentIdentity,
            supervisor.ProductionEnvironmentIdentity,
            supervisor.TestSuiteHash,
            supervisor.ApplyIdentity,
            supervisor.BaseBundleHash,
            supervisor.CandidateBundleHash,
            supervisor.MachineAttestationHash,
            measuredReviewHash,
            decision,
            reviewedAt);

    private static string SupervisorHash(
        string reviewId,
        string operationId,
        string runId,
        string generationProjectId,
        string tenantStableId,
        string sourceEnvironmentIdentity,
        string productionEnvironmentIdentity,
        string testSuiteHash,
        string applyIdentity,
        string baseBundleHash,
        string candidateBundleHash,
        string machineAttestationHash,
        string measuredReviewHash,
        RuntimeLifecyclePrivilegeSupervisorDecision decision,
        DateTimeOffset reviewedAt) =>
        Hash(new
        {
            reviewId = Required(reviewId, nameof(reviewId)),
            policyVersion = CurrentPolicyVersion,
            reviewer = SupervisorReviewer,
            operationId = Required(operationId, nameof(operationId)),
            runId = Required(runId, nameof(runId)),
            generationProjectId = Required(generationProjectId, nameof(generationProjectId)),
            tenantStableId = Required(tenantStableId, nameof(tenantStableId)),
            sourceEnvironmentIdentity = RequiredHash(sourceEnvironmentIdentity, nameof(sourceEnvironmentIdentity)),
            productionEnvironmentIdentity = RequiredHash(productionEnvironmentIdentity, nameof(productionEnvironmentIdentity)),
            testSuiteHash = RequiredHash(testSuiteHash, nameof(testSuiteHash)),
            applyIdentity = Required(applyIdentity, nameof(applyIdentity)),
            baseBundleHash = RequiredHash(baseBundleHash, nameof(baseBundleHash)),
            candidateBundleHash = RequiredHash(candidateBundleHash, nameof(candidateBundleHash)),
            machineAttestationHash = RequiredHash(machineAttestationHash, nameof(machineAttestationHash)),
            measuredReviewHash = RequiredHash(measuredReviewHash, nameof(measuredReviewHash)),
            decision,
            reviewedAt
        });

    private static bool Matches(string? machine, string? supervisor, string? expected) =>
        string.Equals(machine, expected, StringComparison.Ordinal) &&
        string.Equals(supervisor, expected, StringComparison.Ordinal);

    private static bool MatchesHash(string? machine, string? supervisor, string? expected) =>
        string.Equals(machine, expected, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(supervisor, expected, StringComparison.OrdinalIgnoreCase);

    private static byte[] ValidateSecret(string? value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        var lower = normalized.ToLowerInvariant();
        var unsafeTokens = new[]
        {
            "change-me", "changeme", "replace-me", "replace-with", "placeholder", "set-in-secret",
            "example", "default-secret", "dev-secret"
        };
        var distinct = normalized.Distinct().Count();
        var entropy = normalized.Length == 0
            ? 0
            : normalized.GroupBy(character => character)
                .Select(group => (double)group.Count() / normalized.Length)
                .Sum(probability => -probability * Math.Log2(probability));
        var bytes = Encoding.UTF8.GetBytes(normalized);
        if (bytes.Length < 32 ||
            unsafeTokens.Any(lower.Contains) ||
            distinct < 12 ||
            entropy < 3.5 ||
            IsRepeatedPattern(normalized))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException(
                $"{parameterName} must contain at least 32 bytes of non-placeholder, high-entropy secret material.",
                parameterName);
        }

        return bytes;
    }

    private static bool IsRepeatedPattern(string value)
    {
        for (var size = 1; size <= Math.Min(16, value.Length / 2); size++)
        {
            if (value.Length % size == 0 &&
                value.Select((character, index) => character == value[index % size]).All(matches => matches))
            {
                return true;
            }
        }

        return false;
    }

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))).ToLowerInvariant();

    private static string Sign(byte[] secret, string domain, string hash) =>
        Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{domain}\n{hash}"))).ToLowerInvariant();

    private static bool FixedHashEquals(string? expected, string? actual) =>
        IsSha256(expected) && IsSha256(actual) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected!), Convert.FromHexString(actual!));

    private static string RequiredHash(string? value, string parameterName)
    {
        var result = Required(value, parameterName).ToLowerInvariant();
        return IsSha256(result)
            ? result
            : throw new ArgumentException($"{parameterName} must be a lowercase SHA-256 value.", parameterName);
    }

    private static string? OptionalHash(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : RequiredHash(value, parameterName);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Required(string? value, string parameterName)
    {
        var result = value?.Trim();
        if (string.IsNullOrWhiteSpace(result) || result.Length > 256 || result.Any(char.IsControl))
        {
            throw new ArgumentException($"{parameterName} is required and must be bounded safe text.", parameterName);
        }

        return result;
    }

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, nameof(value));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}
