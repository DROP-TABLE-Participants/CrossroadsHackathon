using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation.Validation;
using ErpGenerator.Runtime.GeneratedPrimitives;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleCompilationRules;
using static ErpGenerator.Runtime.Shared.Compilation.Validation.BundleSafetyValidator;

namespace ErpGenerator.Runtime.Shared.Compilation.GeneratedPrimitives;

internal static class GeneratedPrimitiveBundleCompiler
{
    public const int MaximumPrimitiveCount = 128;
    public const int MaximumValueCount = 128;

    public static IReadOnlyList<string> Validate(IReadOnlyList<ErpRuntimePrimitive> primitives)
    {
        var errors = new List<string>();
        if (primitives.Count > MaximumPrimitiveCount)
        {
            errors.Add($"bundle.runtime_primitives cannot contain more than {MaximumPrimitiveCount} items.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var primitive in primitives)
        {
            RequireIdentifier(primitive.Id, "runtime_primitive.id", errors);
            if (!ids.Add(primitive.Id))
            {
                errors.Add($"Duplicate runtime primitive id '{primitive.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(primitive.Type) || primitive.Type.Length > 256)
            {
                errors.Add($"runtime_primitive[{primitive.Id}].type is required and bounded.");
                continue;
            }

            if (primitive.Values.Count > MaximumValueCount)
            {
                errors.Add($"runtime_primitive[{primitive.Id}].values exceeds the bounded member count.");
                continue;
            }

            foreach (var (name, value) in primitive.Values)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
                {
                    errors.Add($"runtime_primitive[{primitive.Id}].values contains an invalid member key.");
                }
                RejectSqlLikeValue(value, $"runtime_primitive[{primitive.Id}].values[{name}]", errors);
            }

            try
            {
                _ = RuntimeGeneratedPrimitiveCompiler.Compile(
                    primitive.Id,
                    primitive.Type,
                    primitive.Values);
            }
            catch (ArgumentException)
            {
                errors.Add($"runtime_primitive[{primitive.Id}] does not match the generated compile-time catalog.");
            }
        }

        return errors;
    }

    public static IReadOnlyList<CompiledGeneratedPrimitive> Compile(
        IReadOnlyList<ErpRuntimePrimitive> primitives) =>
        primitives
            .OrderBy(primitive => primitive.Id, StringComparer.Ordinal)
            .Select(primitive => RuntimeGeneratedPrimitiveCompiler.Compile(
                primitive.Id,
                primitive.Type,
                primitive.Values))
            .Select(instance => new CompiledGeneratedPrimitive(
                instance.InstanceId,
                instance.TypeKey,
                instance.Values))
            .ToArray();

    public static IReadOnlyList<CompiledGeneratedPrimitiveDefinition> Definitions() =>
        RuntimeGeneratedPrimitiveCatalog.Definitions
            .OrderBy(definition => definition.Key, StringComparer.Ordinal)
            .Select(definition => new CompiledGeneratedPrimitiveDefinition(
                definition.Key,
                definition.Kind,
                definition.Members.Select(member => new CompiledGeneratedPrimitiveMemberDefinition(
                    member.Name,
                    member.TypeName,
                    member.Required,
                    member.ConstantValue)).ToArray()))
            .ToArray();

}
