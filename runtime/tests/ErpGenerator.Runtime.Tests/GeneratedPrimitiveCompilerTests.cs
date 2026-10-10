using FluentAssertions;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Tests;

public sealed class GeneratedPrimitiveCompilerTests
{
    private const string TypeKey =
        "ErpGenerator.Runtime.GeneratedPrimitives.RuntimeCapabilityDescriptor";

    [Fact]
    public void Live_bundle_compiler_exposes_the_build_time_generated_catalog()
    {
        var definitions = new ErpBundleCompiler().GetGeneratedPrimitiveDefinitions();

        var definition = definitions.Should().ContainSingle(value => value.Key == TypeKey).Subject;
        definition.Kind.Should().Be("record");
        definition.Members.Select(member => member.Name)
            .Should().Equal("CapabilityKey", "Revision");
        definition.Members.Should().OnlyContain(member => member.Required);
    }

    [Fact]
    public void Live_bundle_compiler_validates_and_emits_generated_primitive_metadata()
    {
        var bundle = MinimalBundle();
        bundle.RuntimePrimitives.Add(new ErpRuntimePrimitive
        {
            Id = "runtime_capability",
            Type = TypeKey,
            Values =
            {
                ["CapabilityKey"] = "workflow.approval-delegation",
                ["Revision"] = 2
            }
        });

        var compiler = new ErpBundleCompiler();
        compiler.Validate(bundle).Should().BeEmpty();
        var compiled = compiler.Compile(bundle);

        var primitive = compiled.GeneratedPrimitives.Should().ContainSingle().Subject;
        primitive.InstanceId.Should().Be("runtime_capability");
        primitive.TypeKey.Should().Be(TypeKey);
        primitive.Values.Should().Contain("CapabilityKey", "workflow.approval-delegation");
        primitive.Values.Should().Contain("Revision", 2);
    }

    [Fact]
    public void Live_bundle_compiler_rejects_unknown_generated_types()
    {
        var bundle = MinimalBundle();
        bundle.RuntimePrimitives.Add(new ErpRuntimePrimitive
        {
            Id = "runtime_capability",
            Type = "ErpGenerator.Runtime.GeneratedPrimitives.Unknown",
            Values = { ["CapabilityKey"] = "workflow.approval-delegation", ["Revision"] = 2 }
        });

        var compile = () => new ErpBundleCompiler().Compile(bundle);

        compile.Should().Throw<BundleCompilationException>()
            .Which.Errors.Should().Contain(error => error.Contains("compile-time catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void Live_bundle_compiler_rejects_missing_or_wrong_typed_members()
    {
        var missing = MinimalBundle();
        missing.RuntimePrimitives.Add(new ErpRuntimePrimitive
        {
            Id = "runtime_capability",
            Type = TypeKey,
            Values = { ["CapabilityKey"] = "workflow.approval-delegation" }
        });
        var wrongType = MinimalBundle();
        wrongType.RuntimePrimitives.Add(new ErpRuntimePrimitive
        {
            Id = "runtime_capability",
            Type = TypeKey,
            Values = { ["CapabilityKey"] = "workflow.approval-delegation", ["Revision"] = "not-a-number" }
        });

        new ErpBundleCompiler().Validate(missing).Should().ContainSingle(error =>
            error.Contains("compile-time catalog", StringComparison.Ordinal));
        new ErpBundleCompiler().Validate(wrongType).Should().ContainSingle(error =>
            error.Contains("compile-time catalog", StringComparison.Ordinal));
    }

    private static ErpBundle MinimalBundle() => new()
    {
        Id = "generated_primitive_test",
        Name = "Generated primitive test",
        Version = "1.0.0",
        Modules =
        [
            new ErpModule
            {
                Id = "core",
                Name = "Core",
                Entities =
                [
                    new ErpEntity
                    {
                        Id = "item",
                        Name = "item",
                        DisplayName = "Item",
                        Display = new ErpEntityDisplay { Primary = "name" },
                        Fields =
                        [
                            new ErpField
                            {
                                Id = "name",
                                Name = "name",
                                DisplayName = "Name",
                                Type = "string",
                                Required = true
                            }
                        ]
                    }
                ]
            }
        ],
        Roles =
        {
            ["admin"] = new ErpRole
            {
                DisplayName = "Administrator",
                Grants = [new ErpGrant { Action = "*", Entity = "*" }]
            }
        }
    };
}
