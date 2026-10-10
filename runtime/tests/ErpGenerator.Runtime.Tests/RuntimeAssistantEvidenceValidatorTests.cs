using FluentAssertions;
using ErpGenerator.Runtime.Services.Assistant;
using ErpGenerator.Runtime.Shared.ArtificialIntelligence;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeAssistantEvidenceValidatorTests
{
    [Fact]
    public void Resolves_only_server_authorized_evidence_and_removes_duplicates()
    {
        var evidence = Evidence("evidence_record", "bundle-a");

        var result = RuntimeAssistantEvidenceValidator.Resolve(
            ["evidence_record", "evidence_record"],
            new Dictionary<string, RuntimeEvidenceReference>
            {
                [evidence.Id] = evidence
            },
            "bundle-a");

        result.Should().ContainSingle().Which.Should().BeSameAs(evidence);
    }

    [Fact]
    public void Rejects_forged_evidence_identifiers()
    {
        var evidence = Evidence("evidence_record", "bundle-a");

        var resolve = () => RuntimeAssistantEvidenceValidator.Resolve(
            ["evidence_forged"],
            new Dictionary<string, RuntimeEvidenceReference>
            {
                [evidence.Id] = evidence
            },
            "bundle-a");

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*authorized bundle snapshot*");
    }

    [Fact]
    public void Rejects_evidence_from_a_stale_bundle()
    {
        var evidence = Evidence("evidence_record", "bundle-a");

        var resolve = () => RuntimeAssistantEvidenceValidator.Resolve(
            [evidence.Id],
            new Dictionary<string, RuntimeEvidenceReference>
            {
                [evidence.Id] = evidence
            },
            "bundle-b");

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*authorized bundle snapshot*");
    }

    [Fact]
    public void Rejects_answers_without_evidence()
    {
        var resolve = () => RuntimeAssistantEvidenceValidator.Resolve(
            [],
            new Dictionary<string, RuntimeEvidenceReference>(),
            "bundle-a");

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*between one and eight*");
    }

    private static RuntimeEvidenceReference Evidence(string id, string bundleHash) => new(
        id,
        RuntimeEvidenceKinds.Record,
        "Order #1",
        "/entities/orders/1",
        DateTimeOffset.UtcNow,
        bundleHash,
        "result-hash");
}
