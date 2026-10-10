using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ErpGenerator.Runtime.GeneratedPrimitives;

public sealed record RuntimeGeneratedPrimitiveMemberDefinition(
    string Name,
    string TypeName,
    bool Required,
    string? ConstantValue);

public sealed record RuntimeGeneratedPrimitiveDefinition(
    string Key,
    string Kind,
    IReadOnlyList<RuntimeGeneratedPrimitiveMemberDefinition> Members);

public sealed record RuntimeGeneratedPrimitiveInstance(
    string InstanceId,
    string TypeKey,
    IReadOnlyDictionary<string, object?> Values);

public static class RuntimeGeneratedPrimitiveCatalog
{
    public static IReadOnlyList<RuntimeGeneratedPrimitiveDefinition> Definitions =>
        RuntimeGeneratedPrimitiveGeneratedCatalog.Definitions;
}

/// <summary>
/// The bounded live execution seam for generated primitive schemas. Candidate
/// source contributes only compile-time catalog metadata. Runtime compilation
/// validates ordinary data against that metadata; it never loads candidate
/// types, discovers them through reflection, or invokes candidate code.
/// </summary>
public static class RuntimeGeneratedPrimitiveCompiler
{
    public static RuntimeGeneratedPrimitiveInstance Compile(
        string instanceId,
        string typeKey,
        IReadOnlyDictionary<string, object?> values)
    {
        var normalizedInstanceId = RequiredIdentifier(instanceId, nameof(instanceId));
        ArgumentNullException.ThrowIfNull(typeKey);
        ArgumentNullException.ThrowIfNull(values);
        var definition = RuntimeGeneratedPrimitiveCatalog.Definitions.SingleOrDefault(candidate =>
            candidate.Key.Equals(typeKey.Trim(), StringComparison.Ordinal));
        if (definition is null)
        {
            throw new ArgumentException("Generated primitive type is not present in the compile-time catalog.", nameof(typeKey));
        }

        var normalized = definition.Kind switch
        {
            "record" => CompileRecord(definition, values),
            "enum" => CompileEnum(definition, values),
            _ => throw new ArgumentException("Generated primitive kind is unsupported.", nameof(typeKey))
        };
        return new RuntimeGeneratedPrimitiveInstance(normalizedInstanceId, definition.Key, normalized);
    }

    private static IReadOnlyDictionary<string, object?> CompileRecord(
        RuntimeGeneratedPrimitiveDefinition definition,
        IReadOnlyDictionary<string, object?> values)
    {
        if (values.Keys.Any(key => definition.Members.All(member =>
                !member.Name.Equals(key, StringComparison.Ordinal))))
        {
            throw new ArgumentException("Generated primitive data contains an unknown member.", nameof(values));
        }

        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in definition.Members)
        {
            if (!values.TryGetValue(member.Name, out var value))
            {
                if (member.Required)
                {
                    throw new ArgumentException("Generated primitive data is missing a required member.", nameof(values));
                }

                normalized.Add(member.Name, null);
                continue;
            }

            if (value is null && member.Required)
            {
                throw new ArgumentException("Generated primitive data contains null for a required member.", nameof(values));
            }

            normalized.Add(member.Name, NormalizeScalar(member.TypeName, value));
        }

        return normalized;
    }

    private static IReadOnlyDictionary<string, object?> CompileEnum(
        RuntimeGeneratedPrimitiveDefinition definition,
        IReadOnlyDictionary<string, object?> values)
    {
        if (values.Count != 1 ||
            !values.TryGetValue("value", out var selected) ||
            selected is not string name ||
            definition.Members.All(member => !member.Name.Equals(name, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Generated enum data must select exactly one declared member.", nameof(values));
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = name };
    }

    private static object? NormalizeScalar(string typeName, object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return typeName switch
            {
                "string" or "global::System.String" when value is string text => text,
                "bool" or "global::System.Boolean" => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                "byte" or "global::System.Byte" => Convert.ToByte(value, CultureInfo.InvariantCulture),
                "sbyte" or "global::System.SByte" => Convert.ToSByte(value, CultureInfo.InvariantCulture),
                "short" or "global::System.Int16" => Convert.ToInt16(value, CultureInfo.InvariantCulture),
                "ushort" or "global::System.UInt16" => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                "int" or "global::System.Int32" => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                "uint" or "global::System.UInt32" => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
                "long" or "global::System.Int64" => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                "ulong" or "global::System.UInt64" => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
                "float" or "global::System.Single" => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                "double" or "global::System.Double" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                "decimal" or "global::System.Decimal" => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                "char" or "global::System.Char" => Convert.ToChar(value, CultureInfo.InvariantCulture),
                "global::System.Guid" when value is Guid guid => guid,
                "global::System.Guid" when value is string guidText => Guid.ParseExact(guidText, "D"),
                "global::System.DateTime" when value is DateTime dateTime => dateTime,
                "global::System.DateTimeOffset" when value is DateTimeOffset dateTimeOffset => dateTimeOffset,
                "global::System.DateOnly" when value is DateOnly dateOnly => dateOnly,
                "global::System.TimeOnly" when value is TimeOnly timeOnly => timeOnly,
                _ => throw new ArgumentException("Generated primitive member type is not supported by the live compiler.")
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException("Generated primitive member value does not match its catalog type.", exception);
        }
    }

    private static string RequiredIdentifier(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Length > 128 ||
            normalized.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.')))
        {
            throw new ArgumentException(
                "Generated primitive identifiers must use lowercase ASCII letters, digits, '.', '-', or '_'.",
                parameterName);
        }

        return normalized;
    }
}
