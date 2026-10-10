using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Services.DeploymentBroker;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeDeploymentBrokerLifecyclePrivilegeTests
{
    internal const string MachineSecret = "M7!vQ2#xL9@pR4$kT8^zN5&wC1*eH6?yS3+uF0=gb";
    internal const string SupervisorSecret = "J4@dW8!mK2#sP7$yV1^qT6&nR9*cL3?xA5+fH0=uz";
    internal static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Direct_broker_bypass_without_attestations_is_rejected_for_named_action_to_direct()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));

        var result = Validate(active, candidate, Request(active, candidate));

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be("lifecycle_attestation_missing");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData(null)]
    public void Broker_computes_named_action_widening_from_active_and_candidate(string? candidateTransitions)
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle(
            'b',
            candidateTransitions is null ? null : new FieldLifecycleDefinition("draft", candidateTransitions));

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate)
            .Should().Contain(change =>
                change.Entity == "document" &&
                change.Field == "status" &&
                change.BaseTransitions == "named_action" &&
                change.CandidateTransitions == candidateTransitions);
    }

    [Fact]
    public void Broker_computes_initial_state_change_even_when_transition_mode_is_unchanged()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("approved", "named_action"));

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate)
            .Should().Contain(change => change.BaseInitial == "draft" && change.CandidateInitial == "approved");
    }

    [Fact]
    public void First_production_candidate_direct_field_requires_sentinel_bound_attestation()
    {
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));
        var request = Request(null, candidate);

        Validate(null, candidate, request).FailureCode.Should().Be("lifecycle_attestation_missing");
        Validate(
                null,
                candidate,
                AttestedRequest(
                    null,
                    candidate,
                    RuntimeLifecyclePrivilegeMachineStatus.Passed,
                    RuntimeLifecyclePrivilegeSupervisorDecision.Approve,
                    Now))
            .IsValid.Should().BeTrue();
        request.ExpectedBaseBundleHash.Should().Be(RuntimeLifecyclePrivilegePromotionRules.NoActiveProductionBundleHash);
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(null, candidate)
            .Should().Contain(change => change.Kind == "direct_authority");
    }

    [Fact]
    public void Candidate_only_named_action_surface_includes_system_owner_authority()
    {
        var active = Bundle('a', lifecycle: null, includeStatus: false);
        var candidate = Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [LifecycleAction()]);

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate)
            .Should().Contain(change => change.Kind == "named_action_authority" && change.Field == "status");
    }

    [Fact]
    public void Existing_direct_field_role_gain_uses_runtime_update_and_default_field_write_semantics()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "direct"));
        var defaultFieldWrite = Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "direct"),
            roles: [new RoleDefinition("operator", "Operator", [EntityGrant("update")])]);
        var blockedByAnotherExplicitField = Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "direct"),
            roles:
            [
                new RoleDefinition(
                    "operator",
                    "Operator",
                    [
                        EntityGrant("update"),
                        new PermissionGrantDefinition(
                            GranularPermissionActions.Write,
                            "document",
                            PermissionGrantScopes.Field,
                            "name")
                    ])
            ]);
        var complete = Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "direct"),
            roles: [DirectRole()]);
        var recordScoped = Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "direct"),
            roles:
            [
                new RoleDefinition(
                    "operator",
                    "Operator",
                    [new PermissionGrantDefinition(
                        "update",
                        "document",
                        PermissionGrantScopes.Record,
                        RecordScope: new RecordScopeDefinition(
                            "created_by",
                            RecordPermissionScopeSources.ActorPrincipal))])
            ]);
        var wildcardComplete = Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "direct"),
            roles:
            [
                new RoleDefinition(
                    "owner",
                    "Owner",
                    [
                        new PermissionGrantDefinition("*", "*", PermissionGrantScopes.Entity),
                        new PermissionGrantDefinition(
                            GranularPermissionActions.Write,
                            "document",
                            PermissionGrantScopes.Field,
                            "status")
                    ])
            ]);

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, defaultFieldWrite)
            .Should().Contain(change => change.Kind == "direct_authority");
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, blockedByAnotherExplicitField)
            .Should().NotContain(change => change.Kind == "direct_authority");
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, complete)
            .Should().Contain(change => change.Kind == "direct_authority");
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, recordScoped)
            .Should().Contain(change => change.Kind == "direct_authority");
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, wildcardComplete)
            .Should().Contain(change => change.Kind == "direct_authority");
    }

    [Fact]
    public void Direct_authority_can_be_composed_from_multiple_runtime_roles()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "direct"));
        var candidate = Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "direct"),
            roles:
            [
                new RoleDefinition(
                    "updater",
                    "Updater",
                    [
                        EntityGrant("update"),
                        new PermissionGrantDefinition(
                            GranularPermissionActions.Write,
                            "document",
                            PermissionGrantScopes.Field,
                            "name")
                    ]),
                new RoleDefinition(
                    "status_writer",
                    "Status writer",
                    [new PermissionGrantDefinition(
                        GranularPermissionActions.Write,
                        "document",
                        PermissionGrantScopes.Field,
                        "status")])
            ]);

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate)
            .Should().Contain(change => change.Kind == "direct_authority");
    }

    [Fact]
    public void Existing_named_action_field_new_complete_role_action_effect_tuple_is_widening()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"), actions: [LifecycleAction()]);
        var candidate = Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [LifecycleAction()],
            roles: [NamedActionRole()]);

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate)
            .Should().Contain(change => change.Kind == "named_action_authority");
    }

    [Fact]
    public void Named_action_role_authority_requires_source_read_and_every_declared_permission()
    {
        var action = CrossEntityLifecycleAction();
        var active = WithRequestEntity(Bundle(
            'a',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [action]));
        var roleWithoutTargetRead = CrossEntityNamedActionRole() with
        {
            Grants = CrossEntityNamedActionRole().Grants.Where(grant =>
                !(grant.Scope == PermissionGrantScopes.Entity &&
                  grant.Entity == "document" &&
                  grant.Action == "read")).ToArray()
        };
        var roleWithoutSourceRead = CrossEntityNamedActionRole() with
        {
            Grants = CrossEntityNamedActionRole().Grants.Where(grant =>
                !(grant.Scope == PermissionGrantScopes.Entity &&
                  grant.Entity == "request" &&
                  grant.Action == "read")).ToArray()
        };

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(
                active,
                WithRequestEntity(Bundle(
                    'b',
                    new FieldLifecycleDefinition("draft", "named_action"),
                    actions: [action],
                    roles: [roleWithoutTargetRead])))
            .Should().NotContain(change => change.Kind == "named_action_authority");
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(
                active,
                WithRequestEntity(Bundle(
                    'b',
                    new FieldLifecycleDefinition("draft", "named_action"),
                    actions: [action],
                    roles: [roleWithoutSourceRead])))
            .Should().NotContain(change => change.Kind == "named_action_authority");
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(
                active,
                WithRequestEntity(Bundle(
                    'b',
                    new FieldLifecycleDefinition("draft", "named_action"),
                    actions: [action],
                    roles: [CrossEntityNamedActionRole()])))
            .Should().Contain(change => change.Kind == "named_action_authority");
    }

    [Fact]
    public void Cross_entity_named_action_authority_binds_source_action_and_target_effect_closure()
    {
        var action = CrossEntityLifecycleAction();
        var active = WithRequestEntity(Bundle(
            'a',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [action]));
        var candidate = WithRequestEntity(Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [action],
            roles: [CrossEntityNamedActionRole()]));

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate)
            .Should().Contain(change => change.Kind == "named_action_authority" && change.Field == "status");
    }

    [Fact]
    public void Record_scoped_source_read_is_counted_as_partial_record_transition_authority()
    {
        var action = CrossEntityLifecycleAction();
        var active = WithRequestEntity(Bundle(
            'a',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [action]));
        var recordScopedRole = CrossEntityNamedActionRole() with
        {
            Grants =
            [
                new PermissionGrantDefinition(
                    "read",
                    "request",
                    PermissionGrantScopes.Record,
                    RecordScope: new RecordScopeDefinition("owner_id", RecordPermissionScopeSources.ActorPrincipal)),
                .. CrossEntityNamedActionRole().Grants.Where(grant =>
                    !(grant.Scope == PermissionGrantScopes.Entity && grant.Entity == "request" && grant.Action == "read"))
            ]
        };
        var candidate = WithRequestEntity(Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [action],
            roles: [recordScopedRole]));

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate)
            .Should().Contain(change => change.Kind == "named_action_authority");
    }

    [Fact]
    public void Direct_and_named_record_scope_broadening_changes_the_authority_surface()
    {
        var scopedDirectRole = new RoleDefinition(
            "operator",
            "Operator",
            [new PermissionGrantDefinition(
                "update",
                "document",
                PermissionGrantScopes.Record,
                RecordScope: new RecordScopeDefinition(
                    "created_by",
                    RecordPermissionScopeSources.ActorPrincipal))]);
        var entityDirectRole = new RoleDefinition("operator", "Operator", [EntityGrant("update")]);
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(
                Bundle('a', new FieldLifecycleDefinition("draft", "direct"), roles: [scopedDirectRole]),
                Bundle('b', new FieldLifecycleDefinition("draft", "direct"), roles: [entityDirectRole]))
            .Should().Contain(change => change.Kind == "direct_authority");

        var action = CrossEntityLifecycleAction();
        var scopedNamedRole = WithRequestReadScope(CrossEntityNamedActionRole(), recordScoped: true);
        var entityNamedRole = WithRequestReadScope(CrossEntityNamedActionRole(), recordScoped: false);
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(
                WithRequestEntity(Bundle(
                    'a',
                    new FieldLifecycleDefinition("draft", "named_action"),
                    actions: [action],
                    roles: [scopedNamedRole])),
                WithRequestEntity(Bundle(
                    'b',
                    new FieldLifecycleDefinition("draft", "named_action"),
                    actions: [action],
                    roles: [entityNamedRole])))
            .Should().Contain(change => change.Kind == "named_action_authority");
    }

    [Fact]
    public void Named_action_authority_can_be_composed_from_multiple_runtime_roles()
    {
        var action = LifecycleAction();
        var active = Bundle(
            'a',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [action]);
        var candidate = Bundle(
            'b',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [action],
            roles:
            [
                new RoleDefinition("reader", "Reader", [EntityGrant("read")]),
                new RoleDefinition(
                    "approver",
                    "Approver",
                    [
                        EntityGrant(ActionPermissionEffectAnalyzer.EffectUpdate),
                        new PermissionGrantDefinition(
                            GranularPermissionActions.EffectWrite,
                            "document",
                            PermissionGrantScopes.Field,
                            "status"),
                        new PermissionGrantDefinition(
                            GranularPermissionActions.Execute,
                            "document",
                            PermissionGrantScopes.Action,
                            "approve_document")
                    ])
            ]);

        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate)
            .Should().Contain(change => change.Kind == "named_action_authority");
    }

    [Fact]
    public void Named_action_surface_binds_visibility_permissions_steps_guards_filters_values_and_schema()
    {
        var activeAction = SurfaceLifecycleAction();
        var active = WithRequestEntity(Bundle(
            'a',
            new FieldLifecycleDefinition("draft", "named_action"),
            actions: [activeAction]));
        var query = activeAction.Steps.Single(step => step.Id == "find_documents");
        var guard = activeAction.Steps.Single(step => step.Id == "guard_draft");
        var update = activeAction.Steps.Single(step => step.Id == "approve");
        var candidates = new[]
        {
            activeAction with { VisibleWhen = [StatusCondition("approved")] },
            activeAction with
            {
                Permissions =
                [
                    .. activeAction.Permissions,
                    new ActionPermissionRequirementDefinition("read", "request")
                ]
            },
            activeAction with
            {
                Steps = ReplaceStep(
                    activeAction,
                    query with
                    {
                        Where = [new ActionFilterDefinition("name", "eq", "broader", null, [])]
                    })
            },
            activeAction with
            {
                Steps = ReplaceStep(
                    activeAction,
                    guard with { Condition = StatusCondition("approved") })
            },
            activeAction with
            {
                Steps = ReplaceStep(
                    activeAction,
                    update with
                    {
                        Set = new Dictionary<string, ActionValueDefinition>
                        {
                            ["status"] = new("rejected", null, null, null)
                        }
                    })
            }
        };

        foreach (var candidateAction in candidates)
        {
            RuntimeLifecyclePrivilegePromotionRules.FindWidening(
                    active,
                    WithRequestEntity(Bundle(
                        'b',
                        new FieldLifecycleDefinition("draft", "named_action"),
                        actions: [candidateAction])))
                .Should().Contain(change => change.Kind == "named_action_authority");
        }

        var schemaCandidate = WithStatusOptions(
            WithRequestEntity(Bundle(
                'b',
                new FieldLifecycleDefinition("draft", "named_action"),
                actions: [activeAction])),
            ["draft", "approved", "rejected"]);
        RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, schemaCandidate)
            .Should().Contain(change => change.Kind == "named_action_authority");
    }

    [Fact]
    public void Stale_machine_and_supervisor_attestations_are_rejected()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));
        var request = AttestedRequest(
            active,
            candidate,
            RuntimeLifecyclePrivilegeMachineStatus.Passed,
            RuntimeLifecyclePrivilegeSupervisorDecision.Approve,
            Now.AddMinutes(-31));

        Validate(active, candidate, request).FailureCode.Should().Be("lifecycle_attestation_stale");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Valid_receipts_bound_to_wrong_base_or_candidate_hash_are_rejected(
        bool wrongBaseHash,
        bool wrongCandidateHash)
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));
        var request = AttestedRequest(
            active,
            candidate,
            RuntimeLifecyclePrivilegeMachineStatus.Passed,
            RuntimeLifecyclePrivilegeSupervisorDecision.Approve,
            Now,
            attestedBaseHash: wrongBaseHash ? new string('c', 64) : null,
            attestedCandidateHash: wrongCandidateHash ? new string('c', 64) : null);

        Validate(active, candidate, request).FailureCode.Should().Be("lifecycle_attestation_lineage_mismatch");
    }

    [Theory]
    [InlineData(RuntimeLifecyclePrivilegeMachineStatus.Failed, RuntimeLifecyclePrivilegeSupervisorDecision.Approve)]
    [InlineData(RuntimeLifecyclePrivilegeMachineStatus.Passed, RuntimeLifecyclePrivilegeSupervisorDecision.Reject)]
    public void Negative_machine_or_supervisor_attestation_is_rejected(
        RuntimeLifecyclePrivilegeMachineStatus machineStatus,
        RuntimeLifecyclePrivilegeSupervisorDecision supervisorDecision)
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));
        var request = AttestedRequest(active, candidate, machineStatus, supervisorDecision, Now);

        Validate(active, candidate, request).FailureCode.Should().Be("lifecycle_attestation_rejected");
    }

    [Fact]
    public void Exact_positive_machine_and_supervisor_attestation_permits_broker_boundary()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));
        var request = AttestedRequest(
            active,
            candidate,
            RuntimeLifecyclePrivilegeMachineStatus.Passed,
            RuntimeLifecyclePrivilegeSupervisorDecision.Approve,
            Now);

        Validate(active, candidate, request).Should().Be(RuntimeLifecyclePrivilegeAttestationValidationResult.Valid);
    }

    [Fact]
    public void Cross_tenant_target_test_suite_or_apply_identity_replay_is_rejected()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));
        var signed = AttestedRequest(
            active,
            candidate,
            RuntimeLifecyclePrivilegeMachineStatus.Passed,
            RuntimeLifecyclePrivilegeSupervisorDecision.Approve,
            Now);

        var crossTenant = signed with
        {
            ProductionTarget = signed.ProductionTarget with
            {
                TenantStableId = "stable-2",
                DatabaseName = "erp_tenant_stable_2_production"
            }
        };
        var wrongSuite = signed with { TestSuiteHash = new string('9', 64) };
        var wrongApply = signed with { LifecyclePrivilegeApplyIdentity = "apply-2" };

        Validate(active, candidate, crossTenant).FailureCode.Should().Be("lifecycle_attestation_lineage_mismatch");
        Validate(active, candidate, wrongSuite).FailureCode.Should().Be("lifecycle_attestation_lineage_mismatch");
        Validate(active, candidate, wrongApply).FailureCode.Should().Be("lifecycle_attestation_lineage_mismatch");
    }

    [Fact]
    public void Tampered_hmac_and_null_malformed_attestation_fail_closed_without_throwing()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));
        var signed = AttestedRequest(
            active,
            candidate,
            RuntimeLifecyclePrivilegeMachineStatus.Passed,
            RuntimeLifecyclePrivilegeSupervisorDecision.Approve,
            Now);
        var tampered = signed with
        {
            LifecyclePrivilegeMachineAttestation = signed.LifecyclePrivilegeMachineAttestation! with
            {
                ControllerReceipt = new string('0', 64)
            }
        };
        var malformed = signed with
        {
            LifecyclePrivilegeMachineAttestation = signed.LifecyclePrivilegeMachineAttestation! with
            {
                PolicyVersion = null!,
                Changes = [null!]
            }
        };

        Validate(active, candidate, tampered).FailureCode.Should().Be("lifecycle_attestation_invalid");
        var act = () => Validate(active, candidate, malformed);
        act.Should().NotThrow();
        act().FailureCode.Should().Be("lifecycle_attestation_invalid");
    }

    [Fact]
    public void Placeholder_low_entropy_or_shared_verifier_secrets_are_rejected_by_final_policy_authority()
    {
        var actPlaceholder = () => new RuntimeLifecyclePrivilegeAttestationAuthority(
            "change-me-machine-secret-change-me-machine-secret",
            SupervisorSecret);
        var actShared = () => new RuntimeLifecyclePrivilegeAttestationAuthority(MachineSecret, MachineSecret);
        var actRepeated = () => new RuntimeLifecyclePrivilegeAttestationAuthority(new string('x', 64), SupervisorSecret);

        actPlaceholder.Should().Throw<ArgumentException>();
        actShared.Should().Throw<ArgumentException>();
        actRepeated.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Development_placeholder_keys_cannot_authorize_a_production_widening()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));
        var signed = AttestedRequest(
            active,
            candidate,
            RuntimeLifecyclePrivilegeMachineStatus.Passed,
            RuntimeLifecyclePrivilegeSupervisorDecision.Approve,
            Now);
        var authorization = RuntimeLifecyclePrivilegePromotionAuthorization.FromBrokerRequest(signed);
        var policy = new RuntimeLifecycleProductionActivationPolicy(
            "change-me-machine-secret-change-me-machine-secret",
            "change-me-supervisor-secret-change-me-supervisor-secret",
            new FixedTimeProvider(Now));

        policy.Validate(active, candidate, authorization).FailureCode
            .Should().Be("lifecycle_attestation_unavailable");
    }

    [Fact]
    public void Machine_and_supervisor_verifier_identities_cannot_be_swapped()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "named_action"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "direct"));
        var signed = AttestedRequest(
            active,
            candidate,
            RuntimeLifecyclePrivilegeMachineStatus.Passed,
            RuntimeLifecyclePrivilegeSupervisorDecision.Approve,
            Now);
        var policy = new RuntimeLifecycleProductionActivationPolicy(
            SupervisorSecret,
            MachineSecret,
            new FixedTimeProvider(Now));

        policy.Validate(
                active,
                candidate,
                RuntimeLifecyclePrivilegePromotionAuthorization.FromBrokerRequest(signed))
            .FailureCode.Should().Be("lifecycle_attestation_invalid");
    }

    [Fact]
    public void Proposal_apply_identity_is_length_prefixed_and_collision_safe()
    {
        RuntimeLifecyclePrivilegePromotionAuthorization.ComputeProposalApplyIdentity("a:b", "c")
            .Should().NotBe(RuntimeLifecyclePrivilegePromotionAuthorization.ComputeProposalApplyIdentity("a", "b:c"));
    }

    [Fact]
    public void Restrictive_promotion_preserves_existing_behavior_without_attestation()
    {
        var active = Bundle('a', new FieldLifecycleDefinition("draft", "direct"));
        var candidate = Bundle('b', new FieldLifecycleDefinition("draft", "named_action"));

        Validate(active, candidate, Request(active, candidate)).IsValid.Should().BeTrue();
    }

    internal static RuntimeLifecyclePrivilegeAttestationValidationResult Validate(
        CompiledBundle? active,
        CompiledBundle candidate,
        RuntimeProductionPromotionBrokerRequest request) =>
        RuntimeDeploymentBrokerService.ValidateLifecyclePrivilegePromotion(
            active,
            candidate,
            request,
            MachineSecret,
            SupervisorSecret,
            new FixedTimeProvider(Now));

    internal static RuntimeProductionPromotionBrokerRequest AttestedRequest(
        CompiledBundle? active,
        CompiledBundle candidate,
        RuntimeLifecyclePrivilegeMachineStatus status,
        RuntimeLifecyclePrivilegeSupervisorDecision decision,
        DateTimeOffset timestamp,
        string? attestedBaseHash = null,
        string? attestedCandidateHash = null,
        string attestationId = "machine-1",
        string reviewId = "supervisor-1")
    {
        var request = Request(active, candidate);
        var envelope = RuntimeLifecyclePrivilegePromotionAuthorization.FromBrokerRequest(request) with
        {
            BaseBundleHash = attestedBaseHash ?? request.ExpectedBaseBundleHash!,
            CandidateBundleHash = attestedCandidateHash ?? candidate.BundleHash
        };
        using var authority = new RuntimeLifecyclePrivilegeAttestationAuthority(
            MachineSecret,
            SupervisorSecret,
            new FixedTimeProvider(Now));
        var machine = authority.RecordMachineAttestation(
            attestationId,
            envelope,
            status,
            RuntimeLifecyclePrivilegePromotionRules.FindWidening(active, candidate),
            timestamp);
        var supervisor = authority.RecordSupervisorAttestation(
            reviewId,
            machine,
            new string('e', 64),
            decision,
            timestamp);
        return request with
        {
            LifecyclePrivilegeMachineAttestation = machine,
            LifecyclePrivilegeSupervisorAttestation = supervisor
        };
    }

    internal static RuntimeProductionPromotionBrokerRequest Request(
        CompiledBundle? active,
        CompiledBundle candidate) =>
        new(
            "operation-1",
            "run-1",
            "project-1",
            Target("staging"),
            Target("production"),
            candidate.BundleId,
            candidate.Version,
            candidate.BundleHash,
            new string('d', 64),
            active?.Version,
            active?.BundleHash ?? RuntimeLifecyclePrivilegePromotionRules.NoActiveProductionBundleHash,
            LifecyclePrivilegeApplyIdentity: "apply-1");

    internal static RuntimeDeploymentTarget Target(string environment) =>
        new(
            "tenant-1",
            "stable-1",
            environment == "staging" ? "acme-staging.erpgenerator.test" : "acme.erpgenerator.test",
            environment,
            $"erp_tenant_stable_1_{environment}");

    internal static CompiledBundle Bundle(
        char hash,
        FieldLifecycleDefinition? lifecycle,
        bool includeStatus = true,
        IReadOnlyList<ActionDefinition>? actions = null,
        IReadOnlyList<RoleDefinition>? roles = null)
    {
        var fields = new List<FieldDefinition>
        {
            new(
                "name",
                "name",
                "Name",
                "string",
                "name",
                "text",
                true,
                false,
                null,
                [],
                null)
        };
        if (includeStatus)
        {
            fields.Add(new FieldDefinition(
                "status",
                "status",
                "Status",
                "enum",
                "status",
                "text",
                true,
                false,
                null,
                ["draft", "approved"],
                null,
                lifecycle));
        }

        return new CompiledBundle(
            "bundle-1",
            "Bundle",
            hash == 'a' ? "1.0.0" : "1.1.0",
            new string(hash, 64),
            [new EntityDefinition(
                "document",
                "core",
                "documents",
                "Documents",
                "documents",
                new EntityDisplayDefinition("name", null),
                fields)],
            [],
            [],
            actions ?? [],
            roles ?? [],
            [],
            new DashboardDefinition([]),
            [],
            []);
    }

    internal static ActionDefinition LifecycleAction() =>
        new(
            "approve_document",
            "Approve",
            "document",
            "server",
            null,
            [],
            [
                new ActionPermissionRequirementDefinition("read", "document"),
                new ActionPermissionRequirementDefinition(ActionPermissionEffectAnalyzer.EffectUpdate, "document")
            ],
            [new ActionStepDefinition(
                "approve",
                "update_current_record",
                null,
                [],
                null,
                null,
                new Dictionary<string, ActionValueDefinition>(),
                null,
                null,
                new Dictionary<string, ActionValueDefinition>
                {
                    ["status"] = new("approved", null, null, null)
                },
                null,
                null,
                new Dictionary<string, ActionValueDefinition>(),
                null)]);

    private static ActionDefinition CrossEntityLifecycleAction() =>
        new(
            "approve_document_from_request",
            "Approve document",
            "request",
            "server",
            null,
            [],
            [
                new ActionPermissionRequirementDefinition("read", "request"),
                new ActionPermissionRequirementDefinition("read", "document"),
                new ActionPermissionRequirementDefinition(ActionPermissionEffectAnalyzer.EffectUpdate, "document")
            ],
            [
                new ActionStepDefinition(
                    "find_documents",
                    "query_records",
                    "document",
                    [],
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null),
                new ActionStepDefinition(
                    "approve_documents",
                    "update_records",
                    null,
                    [],
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null,
                    new ActionRecordsFromDefinition("find_documents"),
                    new Dictionary<string, ActionValueDefinition>
                    {
                        ["status"] = new("approved", null, null, null)
                    },
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null)
            ]);

    internal static RoleDefinition NamedActionRole() =>
        new(
            "operator",
            "Operator",
            [
                EntityGrant("read"),
                EntityGrant(ActionPermissionEffectAnalyzer.EffectUpdate),
                new PermissionGrantDefinition(
                    GranularPermissionActions.EffectWrite,
                    "document",
                    PermissionGrantScopes.Field,
                    "status"),
                new PermissionGrantDefinition(
                    GranularPermissionActions.Execute,
                    "document",
                    PermissionGrantScopes.Action,
                    "approve_document")
            ]);

    internal static RoleDefinition DirectRole() =>
        new(
            "operator",
            "Operator",
            [
                EntityGrant("update"),
                new PermissionGrantDefinition(
                    GranularPermissionActions.Write,
                    "document",
                    PermissionGrantScopes.Field,
                    "status")
            ]);

    private static RoleDefinition CrossEntityNamedActionRole() =>
        new(
            "operator",
            "Operator",
            [
                new PermissionGrantDefinition("read", "request", PermissionGrantScopes.Entity),
                EntityGrant("read"),
                EntityGrant(ActionPermissionEffectAnalyzer.EffectUpdate),
                new PermissionGrantDefinition(
                    GranularPermissionActions.EffectWrite,
                    "document",
                    PermissionGrantScopes.Field,
                    "status"),
                new PermissionGrantDefinition(
                    GranularPermissionActions.Execute,
                    "request",
                    PermissionGrantScopes.Action,
                    "approve_document_from_request")
            ]);

    private static CompiledBundle WithRequestEntity(CompiledBundle bundle) =>
        bundle with
        {
            Entities =
            [
                .. bundle.Entities,
                new EntityDefinition(
                    "request",
                    "core",
                    "requests",
                    "Requests",
                    "requests",
                    new EntityDisplayDefinition("name", null),
                    [
                        new FieldDefinition(
                            "name",
                            "name",
                            "Name",
                            "string",
                            "name",
                            "text",
                            true,
                            false,
                            null,
                            [],
                            null),
                        new FieldDefinition(
                            "owner_id",
                            "owner_id",
                            "Owner",
                            "integer",
                            "owner_id",
                            "bigint",
                            false,
                            false,
                            null,
                            [],
                            null)
                    ])
            ]
        };

    private static RoleDefinition WithRequestReadScope(RoleDefinition role, bool recordScoped) =>
        role with
        {
            Grants =
            [
                recordScoped
                    ? new PermissionGrantDefinition(
                        "read",
                        "request",
                        PermissionGrantScopes.Record,
                        RecordScope: new RecordScopeDefinition(
                            "owner_id",
                            RecordPermissionScopeSources.ActorPrincipal))
                    : new PermissionGrantDefinition("read", "request", PermissionGrantScopes.Entity),
                .. role.Grants.Where(grant =>
                    !(grant.Scope == PermissionGrantScopes.Entity &&
                      grant.Entity == "request" &&
                      grant.Action == "read"))
            ]
        };

    private static ActionDefinition SurfaceLifecycleAction()
    {
        var action = LifecycleAction();
        return action with
        {
            VisibleWhen = [StatusCondition("draft")],
            Steps =
            [
                new ActionStepDefinition(
                    "find_documents",
                    "query_records",
                    "document",
                    [new ActionFilterDefinition("name", "eq", "exact", null, [])],
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null),
                new ActionStepDefinition(
                    "guard_draft",
                    "guard",
                    null,
                    [],
                    StatusCondition("draft"),
                    "Document must be draft.",
                    new Dictionary<string, ActionValueDefinition>(),
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null,
                    null,
                    new Dictionary<string, ActionValueDefinition>(),
                    null),
                action.Steps.Single()
            ]
        };
    }

    private static ActionConditionDefinition StatusCondition(string value) =>
        new(
            "eq",
            new ActionOperandDefinition("status", null, null, null),
            new ActionOperandDefinition(null, value, null, null),
            []);

    private static IReadOnlyList<ActionStepDefinition> ReplaceStep(
        ActionDefinition action,
        ActionStepDefinition replacement) =>
        action.Steps.Select(step => step.Id == replacement.Id ? replacement : step).ToArray();

    private static CompiledBundle WithStatusOptions(
        CompiledBundle bundle,
        IReadOnlyList<string> options) =>
        bundle with
        {
            Entities = bundle.Entities.Select(entity =>
                entity.Id != "document"
                    ? entity
                    : entity with
                    {
                        Fields = entity.Fields.Select(field =>
                            field.Id != "status" ? field : field with { Options = options }).ToArray()
                    }).ToArray()
        };

    private static PermissionGrantDefinition EntityGrant(string action) =>
        new(action, "document", PermissionGrantScopes.Entity);

    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
