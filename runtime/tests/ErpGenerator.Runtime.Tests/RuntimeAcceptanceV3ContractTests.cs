using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeAcceptanceV3ContractTests
{
    // Fork baselines bind the ERP Generator schema namespace. Independently
    // reproduced SHA-256 vectors are retained in docs/rebranding-hash-vectors.json.
    [Fact]
    public void Legacy_v2_hash_behavior_ignores_fields_that_do_not_exist_in_v2()
    {
        var legacy = LegacyDocument();
        var baseline = BundleAcceptanceTestContractHasher.ComputeContractHash(legacy);
        var withV3OnlyData = LegacyDocument();
        withV3OnlyData.Actors.Add(new BundleAcceptanceActor { Id = "operator_one", Roles = ["operator"] });
        withV3OnlyData.Assertions[0] = new BundleAcceptanceAssertion
        {
            Id = "ignored_by_v2",
            AfterStep = "ignored_by_v2",
            Type = "record_count",
            Entity = "job",
            Count = 0
        };

        BundleAcceptanceTestContractHasher.ComputeContractHash(withV3OnlyData).Should().Be(baseline);
    }

    [Fact]
    public void Legacy_v2_contract_and_suite_hashes_match_the_frozen_fork_lineage()
    {
        const string expectedContractHash =
            "943f0ab3fb94b03316d2487916f6a22bc9ff56470f84817708ffd396682e64bd";
        const string expectedSuiteHash =
            "6663647425ee95b13240682dc30fe34e9413187d74484156336dc04fd1ffe124";
        var binding = BundleAcceptanceTestContractHasher.Verify([LegacyDocument()]);

        binding.Tests.Should().ContainSingle().Which.ContractHash.Should().Be(expectedContractHash);
        binding.SuiteHash.Should().Be(expectedSuiteHash);
        binding.WasDeclared.Should().BeFalse();
    }

    [Fact]
    public void V3_default_value_kind_preserves_the_frozen_fork_lineage()
    {
        const string expectedContractHash =
            "5984aed73fc49d32dfc63fb4db70c131137a48158d76cd5a67f43834a9a0c02a";
        const string expectedAssertionHash =
            "8b03f946555546de8ed68942929ddf14c65a18eddeb3dff1b9f17ca28b446bf3";
        const string expectedSuiteHash =
            "2c112b19139081b8faf2685e94cdc847c138ba693bc233ff88b23cc39c5bd6da";
        var document = V3Document();
        var contractHash = BundleAcceptanceTestContractHasher.ComputeContractHash(document);
        var assertionHash = BundleAcceptanceTestContractHasher.ComputeAssertionHash(
            document,
            0,
            document.Assertions[0]);
        var binding = BundleAcceptanceTestContractHasher.Stamp([document]);

        contractHash.Should().Be(expectedContractHash);
        assertionHash.Should().Be(expectedAssertionHash);
        binding.SuiteHash.Should().Be(expectedSuiteHash);
    }

    [Fact]
    public void Evidence_digest_contract_is_kind_bound_canonical_and_null_safe()
    {
        var integerText = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.Integer,
            "001");
        var integerValue = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.Integer,
            1L);
        var decimalText = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.Decimal,
            "100.00");
        var decimalValue = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.Decimal,
            100m);
        var booleanText = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.Boolean,
            "TRUE");
        var booleanValue = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.Boolean,
            true);
        var dateText = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.Date,
            "2026-01-02");
        var dateValue = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.Date,
            new DateOnly(2026, 1, 2));
        var utcDateTime = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.DateTime,
            "2026-01-02T10:00:00+02:00");
        var equivalentDateTime = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.DateTime,
            new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero));
        var casePreserved = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.String,
            "Draft");
        var lowerCase = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.String,
            "draft");
        var stringOne = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.String,
            "1");
        var nullValue = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.String,
            null);
        var literalNull = BundleAcceptanceEvidenceDigestContract.Create(
            BundleAcceptanceEvidenceValueKinds.String,
            "null");

        integerText.Should().Be(integerValue);
        integerText.Schema.Should().Be(BundleAcceptanceEvidenceDigestContract.Schema);
        integerText.Digest.Should().Be(
            "6fd71a2b8e4536b556634044044151dd18eb39eef21ad0e4af44941b28cee192",
            "the published v1 evidence canonicalization must not drift under the same schema");
        decimalText.Should().Be(decimalValue);
        booleanText.Should().Be(booleanValue);
        dateText.Should().Be(dateValue);
        utcDateTime.Should().Be(equivalentDateTime);
        casePreserved.Digest.Should().NotBe(lowerCase.Digest);
        stringOne.Digest.Should().NotBe(integerValue.Digest);
        stringOne.Kind.Should().NotBe(integerValue.Kind);
        nullValue.Kind.Should().Be(BundleAcceptanceEvidenceValueKinds.Null);
        nullValue.Digest.Should().NotBe(literalNull.Digest);
        integerText.Digest.Should().MatchRegex("^[a-f0-9]{64}$");
    }

    [Theory]
    [InlineData("actors")]
    [InlineData("setup.seed_hash")]
    [InlineData("steps[].as_actor")]
    [InlineData("steps[].type=bind_actor_record")]
    [InlineData("assertions[].id")]
    [InlineData("assertions[].after_step")]
    [InlineData("assertions[].workflow_ref")]
    [InlineData("assertions[].task_step")]
    [InlineData("assertions[].audit_step")]
    [InlineData("assertions[].report_ref")]
    [InlineData("assertions[].value_kind")]
    public void V2_verify_rejects_every_non_default_v3_only_dto_field(string field)
    {
        var verify = () => BundleAcceptanceTestContractHasher.Verify(
            [V2DocumentWithV3OnlyField(field)]);

        var thrown = verify.Should().Throw<BundleAcceptanceTestContractException>();
        thrown.Which.Message.Should().Contain($"'{field}'");
        thrown.Which.Message.Should().Contain(BundleAcceptanceTestSchemas.V2);
    }

    [Fact]
    public void V3_verify_rejects_a_suite_with_every_declared_binding_stripped()
    {
        var verify = () => BundleAcceptanceTestContractHasher.Verify([V3Document()]);

        verify.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*must be stamped before verification*");
    }

    [Fact]
    public void V3_verify_rejects_a_partially_declared_suite()
    {
        BundleAcceptanceTestDocument[] documents = [V3Document("first"), V3Document("second")];
        BundleAcceptanceTestContractHasher.Stamp(documents);
        documents[1].Test.ContractHash = string.Empty;

        var verify = () => BundleAcceptanceTestContractHasher.Verify(documents);

        verify.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*every test must declare contract_hash, suite_hash, and suite_index*");
    }

    [Fact]
    public void V3_stamp_is_the_authoring_path_and_produces_a_verifiable_declared_suite()
    {
        var document = V3Document();

        var stamped = BundleAcceptanceTestContractHasher.Stamp([document]);
        var verified = BundleAcceptanceTestContractHasher.Verify([document]);

        stamped.WasDeclared.Should().BeTrue();
        verified.WasDeclared.Should().BeTrue();
        verified.SuiteHash.Should().Be(stamped.SuiteHash);
        verified.Tests.Should().ContainSingle()
            .Which.ContractHash.Should().Be(stamped.Tests.Single().ContractHash);
    }

    [Fact]
    public void V3_actor_assertion_checkpoint_and_seed_are_canonical_contract_inputs()
    {
        var document = V3Document();
        var baseline = BundleAcceptanceTestContractHasher.ComputeContractHash(document);
        var stepHash = BundleAcceptanceTestContractHasher.ComputeStepHash(document, 0, document.Steps[0]);
        var assertionHash = BundleAcceptanceTestContractHasher.ComputeAssertionHash(document, 0, document.Assertions[0]);
        var changedActor = V3Document();
        changedActor.Actors[0] = new BundleAcceptanceActor { Id = "operator_two", Roles = ["operator"] };
        changedActor.Steps[0] = CopyStep(changedActor.Steps[0], asActor: "operator_two");
        var changedCheckpoint = V3Document();
        changedCheckpoint.Assertions[0] = CopyAssertion(changedCheckpoint.Assertions[0], afterStep: "$setup");
        var changedAuditStep = V3Document();
        changedAuditStep.Assertions[0] = CopyAssertion(
            changedAuditStep.Assertions[0],
            auditStep: "authored_rejection_audit");

        baseline.Should().MatchRegex("^[a-f0-9]{64}$");
        stepHash.Should().MatchRegex("^[a-f0-9]{64}$");
        assertionHash.Should().MatchRegex("^[a-f0-9]{64}$");
        BundleAcceptanceTestContractHasher.ComputeContractHash(changedActor).Should().NotBe(baseline);
        BundleAcceptanceTestContractHasher.ComputeContractHash(changedCheckpoint).Should().NotBe(baseline);
        BundleAcceptanceTestContractHasher.ComputeContractHash(changedAuditStep).Should().NotBe(baseline);
    }

    [Fact]
    public async Task Acceptance_yaml_rejects_unknown_properties_instead_of_ignoring_prose()
    {
        var root = Path.Combine(Path.GetTempPath(), $"erpgenerator-strict-acceptance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "tests", "unknown.test.yaml"),
                """
                test:
                  key: strict_test
                  label: Strict test
                setup:
                  seed: demo
                steps: []
                assertions:
                  - type: record_count
                    entity: job
                    count: 0
                    interpret_prose: "pretend this proves a business rule"
                """);

            var load = async () => await new ErpBundleYamlLoader()
                .LoadAcceptanceTestsFromDirectoryAsync(root);

            var thrown = await load.Should().ThrowAsync<BundleCompilationException>();
            thrown.Which.Errors.Should().ContainSingle(error =>
                error.Contains("strict acceptance contract", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("actors")]
    [InlineData("setup.seed_hash")]
    [InlineData("steps[].as_actor")]
    [InlineData("steps[].type=bind_actor_record")]
    [InlineData("assertions[].id")]
    [InlineData("assertions[].after_step")]
    [InlineData("assertions[].workflow_ref")]
    [InlineData("assertions[].task_step")]
    [InlineData("assertions[].audit_step")]
    [InlineData("assertions[].report_ref")]
    public async Task Strict_acceptance_yaml_loader_rejects_v3_only_dto_fields_in_v2(
        string field)
    {
        var load = async () => await LoadAcceptanceYamlAsync(V2YamlWithV3OnlyField(field));

        var thrown = await load.Should().ThrowAsync<BundleCompilationException>();
        thrown.Which.Errors.Should().ContainSingle(error => error.Contains($"'{field}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Strict_acceptance_yaml_loader_rejects_a_fully_unstamped_v3_suite()
    {
        var load = async () => await LoadAcceptanceYamlAsync(UnstampedV3Yaml());

        var thrown = await load.Should().ThrowAsync<BundleCompilationException>();
        thrown.Which.Errors.Should().ContainSingle(error =>
            error.Contains("must be stamped before verification", StringComparison.Ordinal));
    }

    [Fact]
    public void V3_rejects_a_record_ref_produced_by_another_test()
    {
        var test = V3Document();
        test.Steps[0] = new BundleAcceptanceStep
        {
            Id = "update_other_test_record",
            Type = "update_record",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "record_from_previous_test" },
            AsRole = "operator",
            AsActor = "operator_one",
            Set = new Dictionary<string, object?> { ["name"] = "Changed" }
        };

        var validate = () => RuntimeBundleTestRunner.ValidateV3RecordReferences(test, []);

        validate.Should().Throw<RuntimeBundleTestException>()
            .WithMessage("*outside its own seed and preceding successful producers*");
    }

    [Fact]
    public void V3_rejects_a_producer_step_id_that_collides_with_its_seed_ref()
    {
        var test = V3Document();

        var validate = () => RuntimeBundleTestRunner.ValidateV3RecordReferences(
            test,
            ["create_job"]);

        validate.Should().Throw<RuntimeBundleTestException>()
            .WithMessage("*producer step 'create_job' collides with its seed or a preceding producer record ref*");
    }

    [Fact]
    public void V3_rejects_contradictory_actor_roles_and_unbound_report_assertions()
    {
        var contradictoryActor = V3Document();
        contradictoryActor.Steps[0] = new BundleAcceptanceStep
        {
            Id = "create_job",
            Type = "create_record",
            Entity = "job",
            AsRole = "manager",
            AsActor = "operator_one",
            Values = new Dictionary<string, object?> { ["name"] = "Job" }
        };
        var unboundReport = V3Document();
        unboundReport.Assertions[0] = new BundleAcceptanceAssertion
        {
            Id = "report_rows",
            AfterStep = "create_job",
            Type = "report_has_rows",
            Report = "jobs_summary",
            ReportRef = "missing_report_step"
        };

        var actorVerify = () => BundleAcceptanceTestContractHasher.Verify([contradictoryActor]);
        var reportVerify = () => BundleAcceptanceTestContractHasher.Verify([unboundReport]);

        actorVerify.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*one of its explicit roles*");
        reportVerify.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*exact report_ref*");
    }

    [Fact]
    public void V3_bind_actor_record_is_hash_bound_causal_single_role_and_not_a_producer()
    {
        var valid = V3BindDocument();

        var stamped = BundleAcceptanceTestContractHasher.Stamp([valid]);
        var verified = BundleAcceptanceTestContractHasher.Verify([valid]);

        verified.SuiteHash.Should().Be(stamped.SuiteHash);
        BundleAcceptanceTestContractHasher.ComputeStepHash(valid, 1, valid.Steps[1])
            .Should().MatchRegex("^[a-f0-9]{64}$");
        BundleAcceptanceExpectedOutcomeContract.IsRecordProducer(valid.Steps[1]).Should().BeFalse();
    }

    [Fact]
    public void V3_actor_contract_rejects_role_union_ambiguity()
    {
        var document = V3BindDocument();
        document.Actors[0] = new BundleAcceptanceActor
        {
            Id = document.Actors[0].Id,
            Roles = ["operator", "manager"]
        };

        var verify = () => BundleAcceptanceTestContractHasher.Verify([document]);

        verify.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*exactly one role*");
    }

    [Theory]
    [InlineData("missing_producer")]
    [InlineData("later_producer")]
    [InlineData("failed_producer")]
    [InlineData("wrong_entity")]
    [InlineData("run_action_producer")]
    [InlineData("extra_fields")]
    [InlineData("rebind")]
    public void V3_bind_actor_record_rejects_malformed_or_noncausal_contracts(string adversary)
    {
        var document = V3BindDocument();
        var create = document.Steps[0];
        var bind = document.Steps[1];
        switch (adversary)
        {
            case "missing_producer":
                document.Steps[1] = CopyBind(bind, recordRef: "missing");
                break;
            case "later_producer":
                document.Steps.Clear();
                document.Steps.Add(bind);
                document.Steps.Add(create);
                break;
            case "failed_producer":
                document.Steps[0] = new BundleAcceptanceStep
                {
                    Id = create.Id,
                    Type = create.Type,
                    Entity = create.Entity,
                    AsActor = create.AsActor,
                    AsRole = create.AsRole,
                    Values = create.Values,
                    Expected = new BundleAcceptanceExpectedOutcome
                    {
                        Outcome = BundleAcceptanceExpectedOutcomes.PermissionDenied,
                        Code = BundleAcceptanceExpectedOutcomeCodes.PermissionEntityCreate,
                        Target = "job"
                    }
                };
                break;
            case "wrong_entity":
                document.Steps[1] = CopyBind(bind, entity: "work_item");
                break;
            case "run_action_producer":
                document.Steps[0] = new BundleAcceptanceStep
                {
                    Id = create.Id,
                    Type = BundleAcceptanceStepTypes.RunAction,
                    Entity = create.Entity,
                    Record = new BundleRecordSelector { Ref = "source_job" },
                    Action = "create_related_job",
                    AsActor = create.AsActor,
                    AsRole = create.AsRole
                };
                break;
            case "extra_fields":
                document.Steps[1] = CopyBind(bind, action: "smuggled_action");
                break;
            case "rebind":
                document.Steps.Add(CopyBind(bind, id: "bind_operator_again"));
                break;
        }

        var verify = () => BundleAcceptanceTestContractHasher.Verify([document]);

        verify.Should().Throw<BundleAcceptanceTestContractException>();
    }

    [Fact]
    public void V3_test_and_actor_ids_accept_the_exact_128_character_persistence_boundary()
    {
        var testKey = $"t{new string('k', 127)}";
        var actorId = $"a{new string('c', 127)}";
        var document = V3Document(testKey, actorId);

        var stamp = () => BundleAcceptanceTestContractHasher.Stamp([document]);

        stamp.Should().NotThrow();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void V3_test_and_actor_ids_reject_values_beyond_the_128_character_persistence_boundary(
        bool oversizedTestKey)
    {
        var testKey = oversizedTestKey ? $"t{new string('k', 128)}" : "bounded_test";
        var actorId = oversizedTestKey ? "bounded_actor" : $"a{new string('c', 128)}";
        var document = V3Document(testKey, actorId);

        var stamp = () => BundleAcceptanceTestContractHasher.Stamp([document]);

        stamp.Should().Throw<BundleAcceptanceTestContractException>();
    }

    [Fact]
    public void V3_actor_principal_tokens_are_canonical_and_must_reference_an_exact_actor()
    {
        var valid = V3Document();
        valid.Steps[0] = new BundleAcceptanceStep
        {
            Id = "create_job",
            Type = "create_record",
            Entity = "job",
            AsRole = "operator",
            AsActor = "operator_one",
            Values = new Dictionary<string, object?>
            {
                ["name"] = "Job",
                ["owner_principal"] = BundleAcceptanceActorPrincipalToken.For("operator_one")
            }
        };
        valid.Assertions[0] = new BundleAcceptanceAssertion
        {
            Id = "owner_is_actor",
            AfterStep = "create_job",
            Type = "record_field_equals",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            Field = "owner_principal",
            Value = BundleAcceptanceActorPrincipalToken.For("operator_one")
        };
        var baseline = BundleAcceptanceTestContractHasher.ComputeContractHash(valid);

        var unknown = V3Document();
        unknown.Steps[0] = new BundleAcceptanceStep
        {
            Id = "create_job",
            Type = "create_record",
            Entity = "job",
            AsRole = "operator",
            AsActor = "operator_one",
            Values = new Dictionary<string, object?>
            {
                ["name"] = "Job",
                ["owner_principal"] = BundleAcceptanceActorPrincipalToken.For("missing_actor")
            }
        };
        var malformed = V3Document();
        malformed.Assertions[0] = new BundleAcceptanceAssertion
        {
            Id = "owner_is_actor",
            AfterStep = "create_job",
            Type = "record_field_equals",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            Field = "owner_principal",
            Value = "$Actor:operator_one"
        };

        baseline.Should().MatchRegex("^[a-f0-9]{64}$");
        BundleAcceptanceTestContractHasher.ComputeContractHash(unknown).Should().NotBe(baseline);
        var verifyUnknown = () => BundleAcceptanceTestContractHasher.Verify([unknown]);
        var verifyMalformed = () => BundleAcceptanceTestContractHasher.Verify([malformed]);
        verifyUnknown.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*unknown actor principal token*");
        verifyMalformed.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*actor principal token*");
    }

    [Fact]
    public void V3_explicit_null_value_kind_is_hashed_and_closed_to_one_record_field_shape()
    {
        var valid = V3Document();
        valid.Assertions[0] = new BundleAcceptanceAssertion
        {
            Id = "job_optional_value_is_null",
            AfterStep = "create_job",
            Type = "record_field_equals",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            Field = "optional_value",
            ValueKind = BundleAcceptanceEvidenceValueKinds.Null
        };
        var baseline = V3Document();
        baseline.Assertions[0] = new BundleAcceptanceAssertion
        {
            Id = "job_optional_value_is_null",
            AfterStep = "create_job",
            Type = "record_field_equals",
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            Field = "optional_value"
        };

        var stamped = BundleAcceptanceTestContractHasher.Stamp([valid]);
        BundleAcceptanceTestContractHasher.Verify([valid]).SuiteHash.Should().Be(stamped.SuiteHash);
        BundleAcceptanceTestContractHasher.ComputeContractHash(valid).Should()
            .NotBe(BundleAcceptanceTestContractHasher.ComputeContractHash(baseline));

        var malformed = new BundleAcceptanceAssertion[]
        {
            WithValue(valid.Assertions[0], "text", string.Empty, BundleAcceptanceEvidenceValueKinds.Null),
            WithValue(valid.Assertions[0], null, "create_job", BundleAcceptanceEvidenceValueKinds.Null),
            WithValue(valid.Assertions[0], null, string.Empty, "json"),
            new()
            {
                Id = "count_with_value_kind",
                AfterStep = "create_job",
                Type = "record_count",
                Entity = "job",
                Count = 1,
                ValueKind = BundleAcceptanceEvidenceValueKinds.Null
            }
        };
        foreach (var assertion in malformed)
        {
            var document = V3Document();
            document.Assertions[0] = assertion;
            var stamp = () => BundleAcceptanceTestContractHasher.Stamp([document]);
            stamp.Should().Throw<BundleAcceptanceTestContractException>();
        }

        static BundleAcceptanceAssertion WithValue(
            BundleAcceptanceAssertion assertion,
            object? value,
            string valueRef,
            string valueKind) => new()
            {
                Id = assertion.Id,
                AfterStep = assertion.AfterStep,
                Type = assertion.Type,
                Entity = assertion.Entity,
                Record = assertion.Record,
                Field = assertion.Field,
                Value = value,
                ValueRef = valueRef,
                ValueKind = valueKind
            };
    }

    [Theory]
    [InlineData("report_row_count")]
    [InlineData("report_record_absent")]
    [InlineData("dashboard_metric_equals")]
    public void V3_exact_report_assertions_require_the_exact_preceding_call_report_ref(string assertionType)
    {
        var valid = V3ExactReportDocument(assertionType);
        var invalid = V3ExactReportDocument(assertionType);
        BundleAcceptanceTestContractHasher.Stamp([valid]);
        BundleAcceptanceTestContractHasher.Stamp([invalid]);
        invalid.Assertions[0] = CopyAssertion(invalid.Assertions[0], reportRef: "missing_report_call");

        var verifyValid = () => BundleAcceptanceTestContractHasher.Verify([valid]);
        var verifyInvalid = () => BundleAcceptanceTestContractHasher.Verify([invalid]);

        verifyValid.Should().NotThrow();
        verifyInvalid.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*exact report_ref*");
    }

    [Fact]
    public void V3_dashboard_metric_equals_rejects_value_ref_until_identity_metrics_are_supported()
    {
        var document = V3ExactReportDocument("dashboard_metric_equals");
        var assertion = document.Assertions[0];
        document.Assertions[0] = new BundleAcceptanceAssertion
        {
            Id = assertion.Id,
            AfterStep = assertion.AfterStep,
            Type = assertion.Type,
            Report = assertion.Report,
            ReportRef = assertion.ReportRef,
            Metric = assertion.Metric,
            Widget = assertion.Widget,
            ValueRef = "create_job"
        };

        var stamp = () => BundleAcceptanceTestContractHasher.Stamp([document]);

        stamp.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*malformed dashboard_metric_equals contract*");
    }

    [Theory]
    [InlineData("report_row_count")]
    [InlineData("report_record_absent")]
    [InlineData("dashboard_metric_equals")]
    public void V2_rejects_v3_only_exact_report_assertions(string assertionType)
    {
        var legacy = LegacyDocument();
        legacy.Assertions[0] = new BundleAcceptanceAssertion { Type = assertionType };

        var verify = () => BundleAcceptanceTestContractHasher.Verify([legacy]);

        verify.Should().Throw<BundleAcceptanceTestContractException>()
            .WithMessage("*v3-only exact report assertion*");
    }

    private static BundleAcceptanceTestDocument LegacyDocument() => new()
    {
        Test = new BundleAcceptanceTest { Key = "legacy", Label = "Legacy" },
        Setup = new BundleAcceptanceSetup { Seed = "demo" },
        Assertions =
        [
            new BundleAcceptanceAssertion { Type = "record_count", Entity = "job", Count = 0 }
        ]
    };

    private static BundleAcceptanceTestDocument V3Document(
        string key = "v3",
        string actorId = "operator_one")
    {
        var seed = new BundleSeedDocument
        {
            Seed = new BundleSeed { Key = "demo", Label = "Demo" }
        };
        return new BundleAcceptanceTestDocument
        {
            Schema = BundleAcceptanceTestSchemas.V3,
            Test = new BundleAcceptanceTest { Key = key, Label = key },
            Setup = new BundleAcceptanceSetup
            {
                Seed = "demo",
                SeedHash = BundleSeedContractHasher.ComputeHash(seed)
            },
            Actors =
            [
                new BundleAcceptanceActor { Id = actorId, Roles = ["operator"] }
            ],
            Steps =
            [
                new BundleAcceptanceStep
                {
                    Id = "create_job",
                    Type = "create_record",
                    Entity = "job",
                    AsRole = "operator",
                    AsActor = actorId,
                    Values = new Dictionary<string, object?> { ["name"] = "Job" }
                }
            ],
            Assertions =
            [
                new BundleAcceptanceAssertion
                {
                    Id = "job_exists",
                    AfterStep = "create_job",
                    Type = "record_field_equals",
                    Entity = "job",
                    Record = new BundleRecordSelector { Ref = "create_job" },
                    Field = "name",
                    Value = "Job"
                }
            ]
        };
    }

    private static BundleAcceptanceTestDocument V3ExactReportDocument(string assertionType)
    {
        var document = V3Document();
        document.Steps.Add(new BundleAcceptanceStep
        {
            Id = "call_jobs_report",
            Type = "call_report",
            Report = "jobs_summary",
            AsRole = "operator",
            AsActor = "operator_one"
        });
        document.Assertions[0] = ExactReportAssertion(assertionType);
        return document;
    }

    private static BundleAcceptanceTestDocument V3BindDocument()
    {
        var document = V3Document();
        document.Steps.Add(new BundleAcceptanceStep
        {
            Id = "bind_operator_job",
            Type = BundleAcceptanceStepTypes.BindActorRecord,
            Entity = "job",
            Record = new BundleRecordSelector { Ref = "create_job" },
            AsRole = "operator",
            AsActor = "operator_one"
        });
        return document;
    }

    private static BundleAcceptanceStep CopyBind(
        BundleAcceptanceStep step,
        string? id = null,
        string? entity = null,
        string? recordRef = null,
        string? action = null) => new()
        {
            Id = id ?? step.Id,
            Type = step.Type,
            Entity = entity ?? step.Entity,
            Record = new BundleRecordSelector { Ref = recordRef ?? step.Record.Ref },
            Action = action ?? step.Action,
            WorkflowRef = step.WorkflowRef,
            AsRole = step.AsRole,
            AsActor = step.AsActor,
            Values = step.Values,
            Set = step.Set,
            Report = step.Report,
            Expected = step.Expected
        };

    private static BundleAcceptanceAssertion ExactReportAssertion(string assertionType) => new()
    {
        Id = "exact_report_assertion",
        AfterStep = "call_jobs_report",
        Type = assertionType,
        Entity = assertionType == "report_record_absent" ? "job" : string.Empty,
        Record = new BundleRecordSelector
        {
            Ref = assertionType == "report_record_absent" ? "create_job" : string.Empty
        },
        Report = "jobs_summary",
        ReportRef = "call_jobs_report",
        Metric = assertionType switch
        {
            "report_record_absent" => "job",
            "dashboard_metric_equals" => "total_jobs",
            _ => string.Empty
        },
        Widget = assertionType == "dashboard_metric_equals" ? "jobs_total" : string.Empty,
        Count = assertionType == "report_row_count" ? 1 : null,
        Value = assertionType == "dashboard_metric_equals" ? 1 : null
    };

    private static BundleAcceptanceStep CopyStep(BundleAcceptanceStep step, string asActor) => new()
    {
        Id = step.Id,
        Type = step.Type,
        Entity = step.Entity,
        Record = step.Record,
        Action = step.Action,
        WorkflowRef = step.WorkflowRef,
        AsRole = step.AsRole,
        AsActor = asActor,
        Values = step.Values,
        Set = step.Set,
        Report = step.Report,
        Expected = step.Expected
    };

    private static BundleAcceptanceAssertion CopyAssertion(
        BundleAcceptanceAssertion assertion,
        string? afterStep = null,
        string? reportRef = null,
        string? auditStep = null) => new()
        {
            Id = assertion.Id,
            AfterStep = afterStep ?? assertion.AfterStep,
            Type = assertion.Type,
            Entity = assertion.Entity,
            Record = assertion.Record,
            Field = assertion.Field,
            Value = assertion.Value,
            ValueRef = assertion.ValueRef,
            ValueKind = assertion.ValueKind,
            Count = assertion.Count,
            Where = assertion.Where,
            Workflow = assertion.Workflow,
            WorkflowRef = assertion.WorkflowRef,
            Status = assertion.Status,
            TaskStep = assertion.TaskStep,
            Report = assertion.Report,
            ReportRef = reportRef ?? assertion.ReportRef,
            Metric = assertion.Metric,
            Widget = assertion.Widget,
            Event = assertion.Event,
            AuditStep = auditStep ?? assertion.AuditStep,
            PermissionScope = assertion.PermissionScope,
            PermissionAction = assertion.PermissionAction,
            PermissionRole = assertion.PermissionRole,
            PermissionTarget = assertion.PermissionTarget
        };

    private static BundleAcceptanceTestDocument V2DocumentWithV3OnlyField(string field)
    {
        var setup = field == "setup.seed_hash"
            ? new BundleAcceptanceSetup { Seed = "demo", SeedHash = new string('a', 64) }
            : new BundleAcceptanceSetup { Seed = "demo" };
        var actors = field == "actors"
            ? new List<BundleAcceptanceActor>
            {
                new() { Id = "operator_one", Roles = ["operator"] }
            }
            : [];
        var steps = field is "steps[].as_actor" or "steps[].type=bind_actor_record"
            ? new List<BundleAcceptanceStep>
            {
                new()
                {
                    Id = "legacy_step",
                    Type = field == "steps[].type=bind_actor_record" ? "bind_actor_record" : "create_record",
                    Entity = "job",
                    AsRole = "operator",
                    AsActor = field == "steps[].as_actor" ? "operator_one" : string.Empty
                }
            }
            : [];
        var assertion = field switch
        {
            "assertions[].id" => LegacyAssertion(id: "job_count"),
            "assertions[].after_step" => LegacyAssertion(afterStep: "$setup"),
            "assertions[].workflow_ref" => LegacyAssertion(workflowRef: "start_job"),
            "assertions[].task_step" => LegacyAssertion(taskStep: "approval"),
            "assertions[].audit_step" => LegacyAssertion(auditStep: "authored_audit"),
            "assertions[].report_ref" => LegacyAssertion(reportRef: "call_jobs"),
            "assertions[].value_kind" => LegacyAssertion(valueKind: BundleAcceptanceEvidenceValueKinds.Null),
            _ => LegacyAssertion()
        };

        return new BundleAcceptanceTestDocument
        {
            Schema = BundleAcceptanceTestSchemas.V2,
            Test = new BundleAcceptanceTest { Key = "legacy", Label = "Legacy" },
            Setup = setup,
            Actors = actors,
            Steps = steps,
            Assertions = [assertion]
        };
    }

    private static BundleAcceptanceAssertion LegacyAssertion(
        string id = "",
        string afterStep = "",
        string workflowRef = "",
        string taskStep = "",
        string auditStep = "",
        string reportRef = "",
        string valueKind = "") => new()
        {
            Id = id,
            AfterStep = afterStep,
            Type = "record_count",
            Entity = "job",
            Count = 0,
            WorkflowRef = workflowRef,
            TaskStep = taskStep,
            AuditStep = auditStep,
            ReportRef = reportRef,
            ValueKind = valueKind
        };

    private static async Task<IReadOnlyList<BundleAcceptanceTestDocument>> LoadAcceptanceYamlAsync(
        string yaml)
    {
        var root = Path.Combine(Path.GetTempPath(), $"erpgenerator-v3-admission-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "tests", "adversary.test.yaml"), yaml);
            return await new ErpBundleYamlLoader().LoadAcceptanceTestsFromDirectoryAsync(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string V2YamlWithV3OnlyField(string field)
    {
        var actors = field == "actors"
            ? "actors:\n  - id: operator_one\n    roles: [operator]"
            : "actors: []";
        var seedHash = field == "setup.seed_hash"
            ? $"\n  seed_hash: {new string('a', 64)}"
            : string.Empty;
        var steps = field is "steps[].as_actor" or "steps[].type=bind_actor_record"
            ? $"steps:\n  - id: legacy_step\n    type: {(field == "steps[].type=bind_actor_record" ? "bind_actor_record" : "create_record")}\n    entity: job\n    as_role: operator{(field == "steps[].as_actor" ? "\n    as_actor: operator_one" : string.Empty)}"
            : "steps: []";
        var assertionField = field switch
        {
            "assertions[].id" => "\n    id: job_count",
            "assertions[].after_step" => "\n    after_step: $setup",
            "assertions[].workflow_ref" => "\n    workflow_ref: start_job",
            "assertions[].task_step" => "\n    task_step: approval",
            "assertions[].audit_step" => "\n    audit_step: authored_audit",
            "assertions[].report_ref" => "\n    report_ref: call_jobs",
            "assertions[].value_kind" => "\n    value_kind: \"null\"",
            _ => string.Empty
        };

        return $"""
            schema: {BundleAcceptanceTestSchemas.V2}
            test:
              key: legacy
              label: Legacy
            setup:
              seed: demo{seedHash}
            {actors}
            {steps}
            assertions:
              - type: record_count{assertionField}
                entity: job
                count: 0
            """;
    }

    private static string UnstampedV3Yaml() => $"""
        schema: {BundleAcceptanceTestSchemas.V3}
        test:
          key: unstamped_v3
          label: Unstamped V3
        setup:
          seed: demo
          seed_hash: {new string('a', 64)}
        actors:
          - id: operator_one
            roles: [operator]
        steps:
          - id: create_job
            type: create_record
            entity: job
            as_role: operator
            as_actor: operator_one
            values:
              name: Job
        assertions:
          - id: job_exists
            after_step: create_job
            type: record_field_equals
            entity: job
            record:
              ref: create_job
            field: name
            value: Job
        """;
}
