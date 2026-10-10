using System.Text.Json;
using FluentAssertions;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class YamlNumericScalarLoaderTests
{
    private const string HighPrecisionNumber = "0.100000000000000000000000000001";

    [Fact]
    public void Loader_keeps_exact_native_numbers_convertible_and_uses_json_only_for_lossless_fallbacks()
    {
        var bundle = new ErpBundleYamlLoader().Load(
            """
            id: numeric-types
            name: Numeric types
            version: 1.0.0
            modules: []
            runtime_primitives:
              - id: numeric_values
                type: fixture
                values:
                  ordinary_integer: 8
                  long_integer: 2147483648
                  unsigned_integer: 9223372036854775808
                  decimal_max: 79228162514264337593543950335
                  ordinary_decimal: 0.1
                  positive_decimal: +0.25
                  leading_decimal: .5
                  trailing_decimal: 8.
                  exact_exponent: 1e2
                  decimal_min: 1e-28
                  decimal_underflow: 1e-29
                  huge_exponent: 1e9999999999999999999999999999999999999999999
                  high_precision: 0.100000000000000000000000000001
                  quoted_number: "0.1"
                  enabled: true
                  absent: null
            """);

        var values = bundle.RuntimePrimitives.Single().Values;
        values["ordinary_integer"].Should().Be(8);
        values["long_integer"].Should().Be(2147483648L);
        values["unsigned_integer"].Should().Be(9223372036854775808UL);
        values["decimal_max"].Should().Be(decimal.MaxValue);
        values["ordinary_decimal"].Should().Be(0.1m);
        values["positive_decimal"].Should().Be(0.25m);
        values["leading_decimal"].Should().Be(0.5m);
        values["trailing_decimal"].Should().Be(8m);
        values["exact_exponent"].Should().Be(100m);
        values["decimal_min"].Should().Be(0.0000000000000000000000000001m);
        AssertExactJsonNumber(values["decimal_underflow"], "1e-29");
        AssertExactJsonNumber(values["huge_exponent"], "1e9999999999999999999999999999999999999999999");
        AssertExactJsonNumber(values["high_precision"], HighPrecisionNumber);
        values["quoted_number"].Should().Be("0.1");
        values["enabled"].Should().Be(true);
        values["absent"].Should().BeNull();
    }

    [Fact]
    public async Task Folder_bundle_loader_preserves_high_precision_object_numbers()
    {
        var directory = RuntimeBundleTestHelper.CopyItServicesV4FolderToTemp();
        try
        {
            var actionPath = Path.Combine(directory, "actions", "create-invoice-draft.yaml");
            var yaml = await File.ReadAllTextAsync(actionPath);
            var updated = yaml.Replace(
                "        value: 0",
                $"        value: {HighPrecisionNumber}",
                StringComparison.Ordinal);
            updated.Should().NotBe(yaml, "the fixture must still contain the numeric guard value");
            await File.WriteAllTextAsync(actionPath, updated);

            var bundle = await new ErpBundleYamlLoader().LoadDirectoryAsync(directory);

            var value = bundle.Actions
                .Single(action => action.Key == "create_invoice_draft")
                .Steps.Single(step => step.Id == "ensure_timesheets_exist")
                .Condition!.Right!.Value;
            AssertExactJsonNumber(value, HighPrecisionNumber);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Seed_loader_preserves_numeric_precision_without_changing_other_scalar_types()
    {
        var directory = RuntimeBundleTestHelper.CreateTempDirectory();
        try
        {
            var seedPath = Path.Combine(directory, "precision.yaml");
            await File.WriteAllTextAsync(
                seedPath,
                $$"""
                seed:
                  key: precision
                  label: Precision
                records:
                  - ref: sample
                    entity: sample
                    values:
                      amount: .{{HighPrecisionNumber[2..]}}
                      count: 8
                      rate: 0.25
                      quoted: ".{{HighPrecisionNumber[2..]}}"
                      enabled: true
                      absent: null
                """);

            var seed = await new ErpBundleYamlLoader().LoadSeedFileAsync(seedPath);
            var values = seed.Records.Single().Values;

            AssertExactJsonNumber(values["amount"], HighPrecisionNumber);
            values["count"].Should().Be(8);
            values["rate"].Should().Be(0.25m);
            values["quoted"].Should().Be($".{HighPrecisionNumber[2..]}");
            values["enabled"].Should().Be(true);
            values["absent"].Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Strict_acceptance_loader_preserves_high_precision_assertion_values()
    {
        var root = RuntimeBundleTestHelper.CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "tests", "precision.test.yaml"),
                $$"""
                test:
                  key: precision_evidence
                  label: Precision evidence
                setup:
                  seed: demo
                steps: []
                assertions:
                  - type: record_exists
                    entity: invoice
                    where:
                      field: amount
                      op: eq
                      value: +{{HighPrecisionNumber}}
                """);

            var tests = await new ErpBundleYamlLoader().LoadAcceptanceTestsFromDirectoryAsync(root);

            AssertExactJsonNumber(tests.Single().Assertions.Single().Where.Value, HighPrecisionNumber);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertExactJsonNumber(object? value, string expectedRawNumber)
    {
        var element = value.Should().BeOfType<JsonElement>().Subject;
        element.ValueKind.Should().Be(JsonValueKind.Number);
        element.GetRawText().Should().Be(expectedRawNumber);
    }
}
