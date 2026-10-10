using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation.Hashing;
using ErpGenerator.Runtime.Shared.Compilation.GeneratedPrimitives;
using ErpGenerator.Runtime.Shared.Compilation.Metadata;
using ErpGenerator.Runtime.Shared.Compilation.TablePlanning;
using ErpGenerator.Runtime.Shared.Compilation.Validation;

namespace ErpGenerator.Runtime.Shared.Compilation;

public sealed class ErpBundleCompiler
{
    public CompiledBundle Compile(ErpBundle bundle)
    {
        var errors = Validate(bundle);
        if (errors.Count > 0)
        {
            throw new BundleCompilationException(errors);
        }

        var metadata = BundleMetadataCompiler.Compile(bundle);
        var tables = BundleTablePlanBuilder.Build(metadata.Entities, metadata.Roles);
        var generatedPrimitives = GeneratedPrimitiveBundleCompiler.Compile(bundle.RuntimePrimitives);
        var hash = BundleHashCalculator.ComputeHash(bundle, metadata.Schedules);

        return new CompiledBundle(
            bundle.Id,
            bundle.Name,
            bundle.Version,
            hash,
            metadata.Entities,
            metadata.Views,
            metadata.Workflows,
            metadata.Actions,
            metadata.Roles,
            metadata.Reports,
            metadata.Dashboard,
            tables,
            generatedPrimitives)
        {
            Schedules = metadata.Schedules,
            ExceptionRules = metadata.ExceptionRules
        };
    }

    public IReadOnlyList<string> Validate(ErpBundle bundle) =>
        BundleValidator.Validate(bundle)
            .Concat(GeneratedPrimitiveBundleCompiler.Validate(bundle.RuntimePrimitives))
            .ToArray();

    /// <summary>
    /// Returns the compile-time catalog emitted from the isolated data-only
    /// generated source tree. Runtime callers consume bounded metadata only.
    /// </summary>
    public IReadOnlyList<CompiledGeneratedPrimitiveDefinition> GetGeneratedPrimitiveDefinitions() =>
        GeneratedPrimitiveBundleCompiler.Definitions();
}
