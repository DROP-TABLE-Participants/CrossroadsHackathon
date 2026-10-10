using System.Text.Json;

namespace ErpGenerator.Api.Tests;

public sealed class GenerationRoutingPolicyTests
{
    [Theory]
    [InlineData((int)GenerationStageOutcome.Completed, (int)GenerationRoute.Advance)]
    [InlineData((int)GenerationStageOutcome.WaitingForInput, (int)GenerationRoute.WaitForInput)]
    public void Stage_outcome_has_one_deterministic_route(
        int outcome,
        int expected)
    {
        Assert.Equal(
            (GenerationRoute)expected,
            GenerationRoutingPolicy.ForStageOutcome((GenerationStageOutcome)outcome));
    }

    [Theory]
    [InlineData(GenerationFailureDispositions.Retryable, 1, 3, (int)GenerationRoute.Retry)]
    [InlineData(GenerationFailureDispositions.Retryable, 2, 3, (int)GenerationRoute.Retry)]
    [InlineData(GenerationFailureDispositions.Retryable, 3, 3, (int)GenerationRoute.Fail)]
    [InlineData(GenerationFailureDispositions.Terminal, 1, 3, (int)GenerationRoute.Fail)]
    [InlineData(GenerationFailureDispositions.Configuration, 1, 3, (int)GenerationRoute.Fail)]
    public void Failure_route_is_bounded(
        string disposition,
        int failureCount,
        int maximumAttempts,
        int expected)
    {
        var failure = new GenerationFailure(
            disposition,
            "test_failure",
            "Safe customer message.",
            "Safe diagnostic message.");

        Assert.Equal(
            (GenerationRoute)expected,
            GenerationRoutingPolicy.ForFailure(
                failure,
                failureCount,
                maximumAttempts));
    }

    [Fact]
    public void Cancellation_route_is_explicit()
    {
        Assert.Equal(
            GenerationRoute.Cancel,
            GenerationRoutingPolicy.ForRunStatus(
                GenerationRunStatuses.CancellationRequested));
        Assert.Equal(
            GenerationRoute.Advance,
            GenerationRoutingPolicy.ForRunStatus(GenerationRunStatuses.Running));
    }

    [Fact]
    public void Timeout_and_malformed_output_are_classified_without_raw_payloads()
    {
        var timeout = GenerationFailureClassifier.Classify(
            new TimeoutException("provider payload with private evidence"));
        var malformed = GenerationFailureClassifier.Classify(
            new JsonException("customer-secret-payload"));

        Assert.Equal(GenerationFailureDispositions.Retryable, timeout.Disposition);
        Assert.Equal("generation_dependency_unavailable", timeout.Code);
        Assert.DoesNotContain("private evidence", timeout.DiagnosticMessage);
        Assert.Equal(GenerationFailureDispositions.Terminal, malformed.Disposition);
        Assert.Equal("generation_state_invalid", malformed.Code);
        Assert.DoesNotContain("customer-secret-payload", malformed.DiagnosticMessage);
        Assert.DoesNotContain("customer-secret-payload", malformed.CustomerMessage);
    }
}
