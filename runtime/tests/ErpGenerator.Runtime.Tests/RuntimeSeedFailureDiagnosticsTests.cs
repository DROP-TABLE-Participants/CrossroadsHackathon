using FluentAssertions;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Services.DeploymentBroker;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeSeedFailureDiagnosticsTests
{
    [Fact]
    public void Seed_record_diagnostic_is_bounded_and_contains_identifiers_not_values()
    {
        var exception = new RuntimeSeedRecordValidationException(
            new string('r', 200),
            new string('e', 200),
            Enumerable.Range(0, 20).Select(index => $"field-{index}-{new string('f', 80)}"));

        var created = RuntimeSeedFailureDiagnostics.TryCreate(exception, out var diagnostic);

        created.Should().BeTrue();
        diagnostic.FailureCode.Should().Be("seed_validation_failed");
        diagnostic.SafeError.Length.Should().BeLessThanOrEqualTo(RuntimeSeedFailureDiagnostics.MaximumSafeErrorLength);
        diagnostic.Fields.Should().HaveCount(6);
        diagnostic.SafeError.Should().NotContain(new string('r', 57));
        diagnostic.SafeError.Should().NotContain(new string('e', 57));
    }

    [Fact]
    public void Raw_record_validation_messages_are_not_exposed_to_the_customer()
    {
        var exception = new RuntimeRecordValidationException(
        [
            new RuntimeValidationError("customer_id", "Customer contains raw-value-do-not-leak."),
            new RuntimeValidationError("status", "Status contains another-secret-value.")
        ]);

        RuntimeSeedFailureDiagnostics.TryCreate(exception, out var diagnostic).Should().BeTrue();

        diagnostic.FailureCode.Should().Be("seed_validation_failed");
        diagnostic.Fields.Should().Equal("customer_id", "status");
        diagnostic.SafeError.Should().Contain("customer_id").And.Contain("status");
        diagnostic.SafeError.Should().NotContain("raw-value-do-not-leak");
        diagnostic.SafeError.Should().NotContain("another-secret-value");
    }

    [Fact]
    public void Seed_reference_errors_use_a_stable_customer_safe_message()
    {
        var exception = new RuntimeSeedException("Seed ref contains raw-value-do-not-leak.");

        RuntimeSeedFailureDiagnostics.TryCreate(exception, out var diagnostic).Should().BeTrue();

        diagnostic.FailureCode.Should().Be("seed_validation_failed");
        diagnostic.SafeError.Should().Be(
            "Generated staging seed data contains invalid record references or schema fields.");
        diagnostic.SafeError.Should().NotContain("raw-value-do-not-leak");
    }
}
