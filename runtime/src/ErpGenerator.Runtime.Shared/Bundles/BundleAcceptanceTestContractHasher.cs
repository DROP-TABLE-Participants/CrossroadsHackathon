using System.Security.Cryptography;
using System.Text.Json;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Shared.Bundles;

/// <summary>
/// Creates and verifies content-addressed acceptance-test contracts. Source
/// paths are deliberately excluded: moving a contract does not change it, while
/// every executable field and the exact suite order are bound by SHA-256.
/// </summary>
public static class BundleAcceptanceTestContractHasher
{
    private const string V2SuiteSchema = "erpgenerator.bundle-acceptance-suite.v1";
    private const string V3SuiteSchema = "erpgenerator.bundle-acceptance-suite.v2";
    private const string StepSchema = "erpgenerator.bundle-acceptance-step.v1";
    private const string AssertionSchema = "erpgenerator.bundle-acceptance-assertion.v1";

    public static BundleAcceptanceTestSuiteBinding Stamp(
        IReadOnlyList<BundleAcceptanceTestDocument> documents)
    {
        var bindings = CreateBindings(documents, useDeclaredOrder: false);
        var suiteHash = ComputeSuiteHash(bindings);
        foreach (var binding in bindings)
        {
            binding.Document.Test.ContractHash = binding.ContractHash;
            binding.Document.Test.SuiteHash = suiteHash;
            binding.Document.Test.SuiteIndex = binding.SuiteIndex;
        }

        return new BundleAcceptanceTestSuiteBinding(suiteHash, bindings, true);
    }

    public static BundleAcceptanceTestSuiteBinding Verify(
        IReadOnlyList<BundleAcceptanceTestDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            throw new BundleAcceptanceTestContractException(
                "Acceptance-test suites must contain at least one test contract.");
        }

        ValidateDocumentShapes(documents);
        var hasAnyDeclaredBinding = documents.Any(HasAnyDeclaredBinding);
        var hasAllDeclaredBindings = documents.All(HasCompleteDeclaredBinding);
        var isV3Suite = documents.All(IsV3);
        if (isV3Suite && !hasAllDeclaredBindings)
        {
            throw new BundleAcceptanceTestContractException(
                "V3 acceptance-test suites must be stamped before verification; every test must declare contract_hash, suite_hash, and suite_index.");
        }

        if (hasAnyDeclaredBinding && !hasAllDeclaredBindings)
        {
            throw new BundleAcceptanceTestContractException(
                "Acceptance-test suite binding is incomplete; every test must declare contract_hash, suite_hash, and suite_index.");
        }

        var bindings = CreateBindings(documents, useDeclaredOrder: hasAllDeclaredBindings);
        var suiteHash = ComputeSuiteHash(bindings);
        if (hasAllDeclaredBindings)
        {
            foreach (var binding in bindings)
            {
                var declared = binding.Document.Test;
                if (!string.Equals(declared.ContractHash, binding.ContractHash, StringComparison.Ordinal))
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{declared.Key}' contract_hash does not match its canonical contract.");
                }

                if (!string.Equals(declared.SuiteHash, suiteHash, StringComparison.Ordinal))
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{declared.Key}' suite_hash does not match the exact ordered test suite.");
                }
            }
        }

        return new BundleAcceptanceTestSuiteBinding(suiteHash, bindings, hasAllDeclaredBindings);
    }

    public static BundleAcceptanceTestSuiteBinding Verify(
        IReadOnlyList<BundleAcceptanceTestDocument> documents,
        CompiledBundle compiled)
    {
        var binding = Verify(documents);
        var semanticError = BundleAcceptanceRecordProducerContract.FindUnsupportedReason(documents, compiled);
        if (semanticError is not null)
        {
            throw new BundleAcceptanceTestContractException(semanticError);
        }

        return binding;
    }

    public static string ComputeContractHash(BundleAcceptanceTestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", document.Schema);
            writer.WritePropertyName("test");
            writer.WriteStartObject();
            writer.WriteString("key", document.Test.Key);
            writer.WriteString("label", document.Test.Label);
            writer.WriteString("origin", document.Test.Origin);
            writer.WriteEndObject();
            writer.WritePropertyName("setup");
            writer.WriteStartObject();
            writer.WriteString("seed", document.Setup.Seed);
            if (IsV3(document))
            {
                writer.WriteString("seedHash", document.Setup.SeedHash);
            }
            writer.WriteEndObject();
            if (IsV3(document))
            {
                writer.WritePropertyName("actors");
                writer.WriteStartArray();
                foreach (var actor in document.Actors)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", actor.Id);
                    writer.WritePropertyName("roles");
                    writer.WriteStartArray();
                    foreach (var role in actor.Roles)
                    {
                        writer.WriteStringValue(role);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
            writer.WritePropertyName("steps");
            writer.WriteStartArray();
            foreach (var step in document.Steps)
            {
                WriteStep(writer, step, IsV3(document));
            }

            writer.WriteEndArray();
            writer.WritePropertyName("assertions");
            writer.WriteStartArray();
            foreach (var assertion in document.Assertions)
            {
                WriteAssertion(writer, assertion, IsV3(document));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static string ComputeStepHash(
        BundleAcceptanceTestDocument document,
        int index,
        BundleAcceptanceStep step) =>
        ComputeItemHash(StepSchema, document.Test.Key, index, step.Id, writer =>
            WriteStep(writer, step, IsV3(document)));

    public static string ComputeAssertionHash(
        BundleAcceptanceTestDocument document,
        int index,
        BundleAcceptanceAssertion assertion) =>
        ComputeItemHash(AssertionSchema, document.Test.Key, index, assertion.Id, writer =>
            WriteAssertion(writer, assertion, IsV3(document)));

    private static IReadOnlyList<BundleAcceptanceTestContractBinding> CreateBindings(
        IReadOnlyList<BundleAcceptanceTestDocument> documents,
        bool useDeclaredOrder)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            throw new BundleAcceptanceTestContractException(
                "Acceptance-test suites must contain at least one test contract.");
        }

        ValidateDocumentShapes(documents);
        var duplicateKeys = documents
            .GroupBy(document => document.Test.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateKeys is not null)
        {
            throw new BundleAcceptanceTestContractException(
                $"Duplicate acceptance-test key '{duplicateKeys.Key}' is not allowed; controller smoke cannot overwrite an authored test.");
        }

        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.Test.Key))
            {
                throw new BundleAcceptanceTestContractException("Acceptance-test key is required.");
            }

            if (string.IsNullOrWhiteSpace(document.Test.Label))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{document.Test.Key}' requires a label.");
            }

            if (!BundleAcceptanceTestOrigins.IsKnown(document.Test.Origin))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{document.Test.Key}' has unknown origin '{document.Test.Origin}'.");
            }

            foreach (var step in document.Steps)
            {
                if (step is null)
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' contains a malformed null step.");
                }

                var expectedOutcomeError = BundleAcceptanceExpectedOutcomeContract.FindError(step);
                if (expectedOutcomeError is not null)
                {
                    throw new BundleAcceptanceTestContractException(expectedOutcomeError);
                }
            }
        }

        var schemas = documents.Select(document => document.Schema).Distinct(StringComparer.Ordinal).ToArray();
        if (schemas.Length != 1)
        {
            throw new BundleAcceptanceTestContractException(
                "Acceptance-test suites cannot mix contract schema versions.");
        }

        IReadOnlyList<BundleAcceptanceTestDocument> ordered = documents;
        if (useDeclaredOrder)
        {
            var indexes = documents.Select(document => document.Test.SuiteIndex!.Value).ToArray();
            if (indexes.Any(index => index < 0 || index >= documents.Count) ||
                indexes.Distinct().Count() != documents.Count ||
                !indexes.Order().SequenceEqual(Enumerable.Range(0, documents.Count)))
            {
                throw new BundleAcceptanceTestContractException(
                    "Acceptance-test suite_index values must be unique and contiguous from zero.");
            }

            ordered = documents.OrderBy(document => document.Test.SuiteIndex).ToArray();
        }

        return ordered
            .Select((document, index) => new BundleAcceptanceTestContractBinding(
                index,
                document.Test.Key,
                document.Test.Label,
                document.Test.Origin,
                ComputeContractHash(document),
                document)
            {
                StepBindings = document.Steps.Select((step, stepIndex) =>
                    new BundleAcceptanceContractItemBinding(
                        stepIndex,
                        step.Id,
                        ComputeStepHash(document, stepIndex, step))).ToArray(),
                AssertionBindings = document.Assertions.Select((assertion, assertionIndex) =>
                    new BundleAcceptanceContractItemBinding(
                        assertionIndex,
                        assertion.Id,
                        ComputeAssertionHash(document, assertionIndex, assertion))).ToArray()
            })
            .ToArray();
    }

    private static void ValidateDocumentShapes(IReadOnlyList<BundleAcceptanceTestDocument> documents)
    {
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            if (document is null || document.Test is null || document.Setup is null ||
                document.Actors is null || document.Steps is null || document.Assertions is null)
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance-test contract at suite index {index} is malformed.");
            }

            if (!BundleAcceptanceTestSchemas.IsKnown(document.Schema))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{document.Test.Key}' uses unknown contract schema '{document.Schema}'.");
            }

            if (!IsV3(document))
            {
                var v3OnlyField = FindNonDefaultV3OnlyField(document);
                if (v3OnlyField is not null)
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' uses v3-only field '{v3OnlyField}' with schema '{document.Schema}'.");
                }

                if (document.Assertions.Any(assertion =>
                        IsV3OnlyExactReportAssertion(assertion.Type)))
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' uses a v3-only exact report assertion with schema '{document.Schema}'.");
                }
            }

            if (IsV3(document))
            {
                ValidateV3Document(document);
            }
        }
    }

    private static string ComputeSuiteHash(IReadOnlyList<BundleAcceptanceTestContractBinding> bindings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "schema",
                bindings.All(binding => IsV3(binding.Document)) ? V3SuiteSchema : V2SuiteSchema);
            writer.WriteNumber("count", bindings.Count);
            writer.WritePropertyName("contracts");
            writer.WriteStartArray();
            foreach (var binding in bindings)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", binding.SuiteIndex);
                writer.WriteString("key", binding.Key);
                writer.WriteString("contractHash", binding.ContractHash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static bool HasAnyDeclaredBinding(BundleAcceptanceTestDocument document) =>
        !string.IsNullOrWhiteSpace(document.Test.ContractHash) ||
        !string.IsNullOrWhiteSpace(document.Test.SuiteHash) ||
        document.Test.SuiteIndex is not null;

    private static bool HasCompleteDeclaredBinding(BundleAcceptanceTestDocument document) =>
        document.Test.ContractHash is { Length: 64 } &&
        document.Test.SuiteHash is { Length: 64 } &&
        document.Test.SuiteIndex is not null;

    private static string? FindNonDefaultV3OnlyField(BundleAcceptanceTestDocument document)
    {
        if (document.Actors.Count != 0)
        {
            return "actors";
        }

        if (IsNonDefault(document.Setup.SeedHash))
        {
            return "setup.seed_hash";
        }

        if (document.Steps.Any(step => step is not null && IsNonDefault(step.AsActor)))
        {
            return "steps[].as_actor";
        }

        if (document.Steps.Any(step => step is not null &&
                step.Type.Equals(BundleAcceptanceStepTypes.ReferenceSeedRecord, StringComparison.OrdinalIgnoreCase)))
        {
            return "steps[].type=reference_seed_record";
        }

        if (document.Steps.Any(step => step is not null &&
                step.Type.Equals(BundleAcceptanceStepTypes.BindActorRecord, StringComparison.OrdinalIgnoreCase)))
        {
            return "steps[].type=bind_actor_record";
        }

        if (document.Steps.Any(step => step is not null &&
                step.Type.Equals(BundleAcceptanceStepTypes.RunDueSchedule, StringComparison.OrdinalIgnoreCase)))
        {
            return "steps[].type=run_due_schedule";
        }

        if (document.Steps.Any(step => step is not null && IsNonDefault(step.Schedule)))
        {
            return "steps[].schedule";
        }

        if (document.Steps.Any(step => step is not null && IsNonDefault(step.ScheduleTime)))
        {
            return "steps[].schedule_time";
        }

        foreach (var assertion in document.Assertions.Where(assertion => assertion is not null))
        {
            if (IsNonDefault(assertion.Id))
            {
                return "assertions[].id";
            }

            if (IsNonDefault(assertion.AfterStep))
            {
                return "assertions[].after_step";
            }

            if (IsNonDefault(assertion.WorkflowRef))
            {
                return "assertions[].workflow_ref";
            }

            if (IsNonDefault(assertion.TaskStep))
            {
                return "assertions[].task_step";
            }

            if (IsNonDefault(assertion.AuditStep))
            {
                return "assertions[].audit_step";
            }

            if (IsNonDefault(assertion.ReportRef))
            {
                return "assertions[].report_ref";
            }

            if (IsNonDefault(assertion.ValueKind))
            {
                return "assertions[].value_kind";
            }
        }

        return null;
    }

    private static bool IsNonDefault(string? value) => value is { Length: > 0 };

    private static void WriteStep(Utf8JsonWriter writer, BundleAcceptanceStep step, bool v3)
    {
        writer.WriteStartObject();
        writer.WriteString("id", step.Id);
        writer.WriteString("type", step.Type);
        writer.WriteString("entity", step.Entity);
        writer.WriteString("recordRef", step.Record?.Ref ?? string.Empty);
        writer.WriteString("action", step.Action);
        writer.WriteString("workflowRef", step.WorkflowRef);
        writer.WriteString("asRole", step.AsRole);
        if (v3)
        {
            writer.WriteString("asActor", step.AsActor);
        }
        BundleAcceptanceCanonicalJson.WriteDictionary(writer, "values", step.Values);
        BundleAcceptanceCanonicalJson.WriteDictionary(writer, "set", step.Set);
        writer.WriteString("report", step.Report);
        // Schedule execution was added after the original V3 hash contract.
        // Omit the empty semantic default so existing stamped V3 suites retain
        // their identity, while every authored scheduler operation binds both
        // fields into its exact contract hash.
        if (v3 && (!string.IsNullOrEmpty(step.Schedule) || !string.IsNullOrEmpty(step.ScheduleTime)))
        {
            writer.WriteString("schedule", step.Schedule);
            writer.WriteString("scheduleTime", step.ScheduleTime);
        }
        writer.WritePropertyName("expected");
        writer.WriteStartObject();
        writer.WriteString("outcome", step.Expected?.Outcome ?? BundleAcceptanceExpectedOutcomes.Success);
        writer.WriteString("code", step.Expected?.Code ?? string.Empty);
        writer.WriteString("target", step.Expected?.Target ?? string.Empty);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteAssertion(Utf8JsonWriter writer, BundleAcceptanceAssertion assertion, bool v3)
    {
        writer.WriteStartObject();
        if (v3)
        {
            writer.WriteString("id", assertion.Id);
            writer.WriteString("afterStep", assertion.AfterStep);
        }
        writer.WriteString("type", assertion.Type);
        writer.WriteString("entity", assertion.Entity);
        writer.WriteString("recordRef", assertion.Record?.Ref ?? string.Empty);
        writer.WriteString("field", assertion.Field);
        writer.WritePropertyName("value");
        BundleAcceptanceCanonicalJson.WriteValue(writer, assertion.Value);
        writer.WriteString("valueRef", assertion.ValueRef);
        // V3 predates value_kind. Omit its semantic default so previously
        // stamped V3 contracts keep their identity; every non-empty typed
        // discriminator is still hash-bound.
        if (v3 && !string.IsNullOrEmpty(assertion.ValueKind))
        {
            writer.WriteString("valueKind", assertion.ValueKind);
        }
        if (assertion.Count is null)
        {
            writer.WriteNull("count");
        }
        else
        {
            writer.WriteNumber("count", assertion.Count.Value);
        }

        writer.WritePropertyName("where");
        writer.WriteStartObject();
        writer.WriteString("field", assertion.Where?.Field ?? string.Empty);
        writer.WriteString("op", assertion.Where?.Op ?? string.Empty);
        writer.WritePropertyName("value");
        BundleAcceptanceCanonicalJson.WriteValue(writer, assertion.Where?.Value);
        writer.WriteString("valueRef", assertion.Where?.ValueRef ?? string.Empty);
        writer.WriteEndObject();
        writer.WriteString("workflow", assertion.Workflow);
        if (v3)
        {
            writer.WriteString("workflowRef", assertion.WorkflowRef);
        }
        writer.WriteString("status", assertion.Status);
        if (v3)
        {
            writer.WriteString("taskStep", assertion.TaskStep);
        }
        writer.WriteString("report", assertion.Report);
        if (v3)
        {
            writer.WriteString("reportRef", assertion.ReportRef);
        }
        writer.WriteString("metric", assertion.Metric);
        writer.WriteString("widget", assertion.Widget);
        writer.WriteString("event", assertion.Event);
        if (v3 && !string.IsNullOrEmpty(assertion.AuditStep))
        {
            writer.WriteString("auditStep", assertion.AuditStep);
        }
        writer.WriteString("permissionScope", assertion.PermissionScope);
        writer.WriteString("permissionAction", assertion.PermissionAction);
        writer.WriteString("permissionRole", assertion.PermissionRole);
        writer.WriteString("permissionTarget", assertion.PermissionTarget);
        writer.WriteEndObject();
    }

    private static string ComputeItemHash(
        string schema,
        string testKey,
        int index,
        string id,
        Action<Utf8JsonWriter> writeItem)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", schema);
            writer.WriteString("testKey", testKey);
            writer.WriteNumber("index", index);
            writer.WriteString("id", id);
            writer.WritePropertyName("contract");
            writeItem(writer);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void ValidateV3Document(BundleAcceptanceTestDocument document)
    {
        if (!IsStableId(document.Test.Key))
        {
            throw new BundleAcceptanceTestContractException(
                "V3 acceptance-test keys must be stable identifiers no longer than 128 characters.");
        }

        if (string.IsNullOrWhiteSpace(document.Setup.Seed) || !IsSha256(document.Setup.SeedHash))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{document.Test.Key}' v3 setup requires an exact seed key and seed_hash.");
        }

        if (document.Steps.Count > 0 && document.Actors.Count == 0)
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{document.Test.Key}' v3 requires at least one explicit actor.");
        }

        var actorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in document.Actors)
        {
            if (!IsStableId(actor.Id) || !actorIds.Add(actor.Id) ||
                actor.Roles.Count != 1 || actor.Roles.Any(role => !IsStableId(role)))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{document.Test.Key}' contains an invalid or duplicate v3 actor; every actor must declare exactly one role.");
            }
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        if (document.Steps.Any(step => !IsStableId(step.Id) || !stepIds.Add(step.Id)))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{document.Test.Key}' contains an invalid or duplicate v3 step id.");
        }
        var stepIndexes = document.Steps.Select((step, index) => (step.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var successfulRecordProducers = new Dictionary<string, BundleAcceptanceStep>(StringComparer.Ordinal);
        var boundActorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (step, stepIndex) in document.Steps.Select((step, index) => (step, index)))
        {
            var actor = document.Actors.FirstOrDefault(candidate => candidate.Id.Equals(step.AsActor, StringComparison.Ordinal));
            if (actor is null || string.IsNullOrWhiteSpace(step.AsRole) ||
                !actor.Roles.Contains(step.AsRole, StringComparer.Ordinal))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{document.Test.Key}' step '{step.Id}' must reference an exact v3 actor and one of its explicit roles.");
            }

            ValidateActorPrincipalTokens(
                document.Test.Key,
                $"step '{step.Id}' values",
                step.Values.Values.Concat(step.Set.Values),
                actorIds);

            if (step.Type.Equals(BundleAcceptanceStepTypes.ReferenceSeedRecord, StringComparison.OrdinalIgnoreCase))
            {
                ValidateReferenceSeedRecordStep(document.Test.Key, step);
            }

            if (step.Type.Equals(BundleAcceptanceStepTypes.BindActorRecord, StringComparison.OrdinalIgnoreCase))
            {
                ValidateBindActorRecordStep(
                    document.Test.Key,
                    step,
                    actor,
                    successfulRecordProducers,
                    boundActorIds);
            }

            if (step.Type.Equals(BundleAcceptanceStepTypes.RunDueSchedule, StringComparison.OrdinalIgnoreCase))
            {
                ValidateRunDueScheduleStep(document.Test.Key, step);
            }
            else if (!string.IsNullOrWhiteSpace(step.Schedule) || !string.IsNullOrWhiteSpace(step.ScheduleTime))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{document.Test.Key}' step '{step.Id}' cannot declare schedule or schedule_time for type '{step.Type}'.");
            }

            var isTaskDecision = step.Type.Equals("approve_task", StringComparison.OrdinalIgnoreCase) ||
                                 step.Type.Equals("reject_task", StringComparison.OrdinalIgnoreCase);
            if (isTaskDecision)
            {
                var starts = document.Steps.Select((candidate, index) => (Candidate: candidate, Index: index))
                    .Where(item => item.Candidate.Id.Equals(step.WorkflowRef, StringComparison.Ordinal) &&
                                   (item.Candidate.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase) ||
                                    item.Candidate.Type.Equals(BundleAcceptanceStepTypes.RunDueSchedule, StringComparison.OrdinalIgnoreCase) &&
                                    item.Candidate.ScheduleTime.Equals(BundleAcceptanceScheduleTimes.Due, StringComparison.Ordinal)))
                    .ToArray();
                if (string.IsNullOrWhiteSpace(step.Record?.Ref) ||
                    starts is not [{ } start] || start.Index >= stepIndex ||
                    !BundleAcceptanceExpectedOutcomeContract.IsSuccess(start.Candidate) ||
                    !string.Equals(start.Candidate.Entity, step.Entity, StringComparison.Ordinal) ||
                    !string.Equals(start.Candidate.Record?.Ref, step.Record?.Ref, StringComparison.Ordinal))
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' task step '{step.Id}' must bind one exact preceding successful workflow_ref start or due schedule for the same entity and record.");
                }
            }
            else if (!string.Equals(step.WorkflowRef, string.Empty, StringComparison.Ordinal))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{document.Test.Key}' step '{step.Id}' must set workflow_ref to the empty string because step type '{step.Type}' does not consume a workflow instance.");
            }

            if (BundleAcceptanceExpectedOutcomeContract.IsRecordProducer(step) &&
                BundleAcceptanceExpectedOutcomeContract.IsSuccess(step))
            {
                successfulRecordProducers[step.Id] = step;
            }
        }

        var assertionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assertion in document.Assertions)
        {
            if (!IsStableId(assertion.Id) || !assertionIds.Add(assertion.Id) ||
                assertion.AfterStep != BundleAcceptanceAssertionCheckpoints.Setup &&
                !stepIds.Contains(assertion.AfterStep))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{document.Test.Key}' contains an invalid v3 assertion id or after_step checkpoint.");
            }

            ValidateActorPrincipalTokens(
                document.Test.Key,
                $"assertion '{assertion.Id}' value",
                [assertion.Value, assertion.Where?.Value],
                actorIds);

            var isRecordFieldEquals = assertion.Type.Equals(
                "record_field_equals",
                StringComparison.OrdinalIgnoreCase);
            var hasExplicitNull = string.Equals(
                assertion.ValueKind,
                BundleAcceptanceEvidenceValueKinds.Null,
                StringComparison.Ordinal);
            if (isRecordFieldEquals)
            {
                var validExpectedValue = hasExplicitNull
                    ? assertion.Value is null && string.IsNullOrWhiteSpace(assertion.ValueRef)
                    : string.IsNullOrEmpty(assertion.ValueKind) &&
                      HasExactlyOneExpectedValue(assertion.Value, assertion.ValueRef);
                if (!validExpectedValue)
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' assertion '{assertion.Id}' has a malformed record_field_equals value contract.");
                }
            }
            else if (!string.IsNullOrEmpty(assertion.ValueKind))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{document.Test.Key}' assertion '{assertion.Id}' cannot declare value_kind for type '{assertion.Type}'.");
            }

            if (assertion.Type.Equals("workflow_status", StringComparison.OrdinalIgnoreCase) ||
                assertion.Type.Equals("task_exists", StringComparison.OrdinalIgnoreCase) ||
                assertion.Type.Equals("task_count", StringComparison.OrdinalIgnoreCase) ||
                assertion.Type.Equals("notification_exists", StringComparison.OrdinalIgnoreCase))
            {
                var start = document.Steps.FirstOrDefault(step =>
                    step.Id.Equals(assertion.WorkflowRef, StringComparison.Ordinal) &&
                    (step.Type.Equals("start_workflow_action", StringComparison.OrdinalIgnoreCase) ||
                     step.Type.Equals(BundleAcceptanceStepTypes.RunDueSchedule, StringComparison.OrdinalIgnoreCase) &&
                     step.ScheduleTime.Equals(BundleAcceptanceScheduleTimes.Due, StringComparison.Ordinal)));
                if (start is null)
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' assertion '{assertion.Id}' requires an exact workflow_ref.");
                }

                else if (!start.Entity.Equals(assertion.Entity, StringComparison.Ordinal) ||
                         !string.Equals(start.Record?.Ref, assertion.Record?.Ref, StringComparison.Ordinal) ||
                         assertion.AfterStep == BundleAcceptanceAssertionCheckpoints.Setup ||
                         stepIndexes[start.Id] > stepIndexes[assertion.AfterStep])
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' assertion '{assertion.Id}' workflow_ref must bind the same exact entity and record before its after_step checkpoint.");
                }
            }

            if (assertion.Type.Equals("workflow_instance_count", StringComparison.OrdinalIgnoreCase))
            {
                var recordRef = assertion.Record?.Ref ?? string.Empty;
                var checkpoint = document.Steps.FirstOrDefault(step =>
                    step.Id.Equals(assertion.AfterStep, StringComparison.Ordinal));
                var isActionStart = checkpoint?.Type.Equals(
                    "start_workflow_action",
                    StringComparison.OrdinalIgnoreCase) == true;
                var isScheduleStart = checkpoint?.Type.Equals(
                                          BundleAcceptanceStepTypes.RunDueSchedule,
                                          StringComparison.OrdinalIgnoreCase) == true &&
                                      BundleAcceptanceScheduleTimes.IsKnown(checkpoint.ScheduleTime);
                if (!IsCanonicalEntityId(assertion.Entity) ||
                    string.IsNullOrWhiteSpace(assertion.Workflow) ||
                    !IsStableId(assertion.Workflow) ||
                    assertion.Count is null or < 0 ||
                    !successfulRecordProducers.TryGetValue(recordRef, out var producer) ||
                    (producer.Type.Equals(BundleAcceptanceStepTypes.ReferenceSeedRecord, StringComparison.OrdinalIgnoreCase) ||
                     producer.Type.Equals(BundleAcceptanceStepTypes.CreateRecord, StringComparison.OrdinalIgnoreCase)) &&
                    !string.Equals(producer.Entity, assertion.Entity, StringComparison.Ordinal) ||
                    checkpoint is null ||
                    !string.Equals(checkpoint.Entity, assertion.Entity, StringComparison.Ordinal) ||
                    !string.Equals(checkpoint.Record?.Ref, recordRef, StringComparison.Ordinal) ||
                    !(isActionStart && string.IsNullOrWhiteSpace(assertion.WorkflowRef) &&
                      !string.IsNullOrWhiteSpace(checkpoint.Action) ||
                      isScheduleStart && assertion.WorkflowRef.Equals(checkpoint.Id, StringComparison.Ordinal) &&
                      !string.IsNullOrWhiteSpace(checkpoint.Schedule) &&
                      (checkpoint.ScheduleTime.Equals(BundleAcceptanceScheduleTimes.Due, StringComparison.Ordinal) ||
                       checkpoint.ScheduleTime.Equals(BundleAcceptanceScheduleTimes.BeforeDue, StringComparison.Ordinal) &&
                       assertion.Count == 0)))
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' assertion '{assertion.Id}' has a malformed workflow_instance_count contract.");
                }
            }

            ValidateExactReportAssertionShape(document.Test.Key, assertion);

            if (RequiresExactReportRef(assertion.Type))
            {
                var reportStep = document.Steps.FirstOrDefault(step =>
                    step.Id.Equals(assertion.ReportRef, StringComparison.Ordinal) &&
                    step.Type.Equals("call_report", StringComparison.OrdinalIgnoreCase) &&
                    step.Report.Equals(assertion.Report, StringComparison.Ordinal));
                if (reportStep is null)
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' assertion '{assertion.Id}' requires an exact report_ref.");
                }

                else if (!assertion.AfterStep.Equals(reportStep.Id, StringComparison.Ordinal))
                {
                    throw new BundleAcceptanceTestContractException(
                        $"Acceptance test '{document.Test.Key}' assertion '{assertion.Id}' must execute immediately at its exact report_ref checkpoint.");
                }
            }
        }
    }

    private static void ValidateBindActorRecordStep(
        string testKey,
        BundleAcceptanceStep step,
        BundleAcceptanceActor actor,
        IReadOnlyDictionary<string, BundleAcceptanceStep> successfulRecordProducers,
        HashSet<string> boundActorIds)
    {
        var recordRef = step.Record?.Ref ?? string.Empty;
        if (!IsCanonicalEntityId(step.Entity) ||
            string.IsNullOrWhiteSpace(recordRef) ||
            !successfulRecordProducers.TryGetValue(recordRef, out var producer) ||
            !(producer.Type.Equals(BundleAcceptanceStepTypes.CreateRecord, StringComparison.OrdinalIgnoreCase) ||
              producer.Type.Equals(
                  BundleAcceptanceStepTypes.ReferenceSeedRecord,
                  StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(producer.Entity, step.Entity, StringComparison.Ordinal))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{testKey}' bind_actor_record step '{step.Id}' must reference a preceding successful create_record or reference_seed_record producer of its exact canonical entity.");
        }

        if (actor.Roles.Count != 1 ||
            !string.Equals(actor.Roles[0], step.AsRole, StringComparison.Ordinal))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{testKey}' bind_actor_record step '{step.Id}' must use its actor's exact single declared role.");
        }

        if (!BundleAcceptanceExpectedOutcomeContract.IsSuccess(step))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{testKey}' bind_actor_record step '{step.Id}' must expect success.");
        }

        if (!string.IsNullOrWhiteSpace(step.Action) ||
            !string.IsNullOrWhiteSpace(step.WorkflowRef) ||
            step.Values is not { Count: 0 } ||
            step.Set is not { Count: 0 } ||
            !string.IsNullOrWhiteSpace(step.Report))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{testKey}' bind_actor_record step '{step.Id}' cannot declare action, workflow_ref, values, set, or report.");
        }

        if (!boundActorIds.Add(step.AsActor))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{testKey}' cannot bind actor '{step.AsActor}' more than once.");
        }
    }

    private static void ValidateReferenceSeedRecordStep(
        string testKey,
        BundleAcceptanceStep step)
    {
        if (!IsCanonicalEntityId(step.Entity) ||
            string.IsNullOrWhiteSpace(step.Record?.Ref) ||
            !IsStableId(step.Record.Ref) ||
            !BundleAcceptanceExpectedOutcomeContract.IsSuccess(step) ||
            !string.IsNullOrWhiteSpace(step.Action) ||
            !string.IsNullOrWhiteSpace(step.WorkflowRef) ||
            step.Values is not { Count: 0 } ||
            step.Set is not { Count: 0 } ||
            !string.IsNullOrWhiteSpace(step.Report) ||
            !string.IsNullOrWhiteSpace(step.Schedule) ||
            !string.IsNullOrWhiteSpace(step.ScheduleTime))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{testKey}' reference_seed_record step '{step.Id}' must alias one exact hash-bound seed ref for its canonical entity without mutating it.");
        }
    }

    private static void ValidateRunDueScheduleStep(
        string testKey,
        BundleAcceptanceStep step)
    {
        if (!IsCanonicalEntityId(step.Entity) ||
            !IsStableId(step.Schedule) ||
            !BundleAcceptanceScheduleTimes.IsKnown(step.ScheduleTime) ||
            string.IsNullOrWhiteSpace(step.Record?.Ref))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{testKey}' run_due_schedule step '{step.Id}' requires one exact canonical entity, schedule, closed schedule_time, and causally available record ref.");
        }

        if (!BundleAcceptanceExpectedOutcomeContract.IsSuccess(step) ||
            !string.IsNullOrWhiteSpace(step.Action) ||
            !string.IsNullOrWhiteSpace(step.WorkflowRef) ||
            step.Values is not { Count: 0 } ||
            step.Set is not { Count: 0 } ||
            !string.IsNullOrWhiteSpace(step.Report))
        {
            throw new BundleAcceptanceTestContractException(
                $"Acceptance test '{testKey}' run_due_schedule step '{step.Id}' must expect success and cannot declare action, workflow_ref, values, set, or report.");
        }
    }

    private static void ValidateExactReportAssertionShape(
        string testKey,
        BundleAcceptanceAssertion assertion)
    {
        switch (assertion.Type.Trim().ToLowerInvariant())
        {
            case "report_row_count" when
                string.IsNullOrWhiteSpace(assertion.Report) ||
                assertion.Count is null or < 0:
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{testKey}' assertion '{assertion.Id}' has a malformed report_row_count contract.");

            case "report_record_absent" when
                string.IsNullOrWhiteSpace(assertion.Entity) ||
                string.IsNullOrWhiteSpace(assertion.Record?.Ref) ||
                string.IsNullOrWhiteSpace(assertion.Report) ||
                string.IsNullOrWhiteSpace(assertion.Metric):
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{testKey}' assertion '{assertion.Id}' has a malformed report_record_absent contract.");

            case "dashboard_metric_equals" when
                string.IsNullOrWhiteSpace(assertion.Widget) ||
                string.IsNullOrWhiteSpace(assertion.Report) ||
                string.IsNullOrWhiteSpace(assertion.Metric) ||
                !string.IsNullOrWhiteSpace(assertion.ValueRef) ||
                !HasExactlyOneExpectedValue(assertion.Value, assertion.ValueRef):
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{testKey}' assertion '{assertion.Id}' has a malformed dashboard_metric_equals contract.");
        }
    }

    private static bool RequiresExactReportRef(string type) =>
        type.Equals("report_has_rows", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("report_metric_equals", StringComparison.OrdinalIgnoreCase) ||
        IsV3OnlyExactReportAssertion(type);

    private static bool IsV3OnlyExactReportAssertion(string type) =>
        type.Equals("report_row_count", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("report_record_absent", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("dashboard_metric_equals", StringComparison.OrdinalIgnoreCase);

    private static bool HasExactlyOneExpectedValue(object? value, string valueRef) =>
        (value is not null) ^ !string.IsNullOrWhiteSpace(valueRef);

    private static void ValidateActorPrincipalTokens(
        string testKey,
        string location,
        IEnumerable<object?> values,
        IReadOnlySet<string> actorIds)
    {
        foreach (var token in values.SelectMany(BundleAcceptanceActorPrincipalToken.CandidatesIn))
        {
            if (!BundleAcceptanceActorPrincipalToken.TryParse(token, out var actorId) || !actorIds.Contains(actorId))
            {
                throw new BundleAcceptanceTestContractException(
                    $"Acceptance test '{testKey}' {location} contains malformed or unknown actor principal token '{token}'.");
            }
        }
    }

    private static bool IsV3(BundleAcceptanceTestDocument document) =>
        string.Equals(document.Schema, BundleAcceptanceTestSchemas.V3, StringComparison.Ordinal);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsStableId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.');

    private static bool IsCanonicalEntityId(string? value) =>
        value is { Length: >= 2 and <= 63 } &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

public sealed record BundleAcceptanceTestSuiteBinding(
    string SuiteHash,
    IReadOnlyList<BundleAcceptanceTestContractBinding> Tests,
    bool WasDeclared);

public sealed record BundleAcceptanceTestContractBinding(
    int SuiteIndex,
    string Key,
    string Label,
    string Origin,
    string ContractHash,
    BundleAcceptanceTestDocument Document)
{
    public IReadOnlyList<BundleAcceptanceContractItemBinding> StepBindings { get; init; } = [];

    public IReadOnlyList<BundleAcceptanceContractItemBinding> AssertionBindings { get; init; } = [];
}

public sealed record BundleAcceptanceContractItemBinding(
    int Index,
    string Id,
    string ContractHash);

public sealed class BundleAcceptanceTestContractException(string message) : Exception(message);
