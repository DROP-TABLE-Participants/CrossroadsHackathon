using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ErpGenerator.Runtime.Shared.Compilation.Hashing;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace ErpGenerator.Runtime.Shared.Compilation;

/// <summary>
/// Keeps the source precision of plain decimal YAML scalars assigned to
/// object-typed bundle values. YamlDotNet otherwise infers these values as
/// Single, which can make distinct authored bundle values hash identically.
/// </summary>
internal sealed partial class LosslessObjectNumericScalarNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser parser,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value,
        ObjectDeserializer rootDeserializer)
    {
        if (expectedType != typeof(object) ||
            parser.Current is not Scalar { Style: ScalarStyle.Plain } scalar ||
            !HasImplicitOrNumericTag(scalar) ||
            !TryNormalizeJsonNumber(scalar.Value, out var normalizedNumber))
        {
            value = null;
            return false;
        }

        value = TryCreateExactNativeNumber(normalizedNumber, out var nativeNumber)
            ? nativeNumber
            : CreateJsonNumber(normalizedNumber);
        parser.MoveNext();
        return true;
    }

    private static bool TryCreateExactNativeNumber(string normalizedNumber, out object? value)
    {
        var hasFractionOrExponent = normalizedNumber.IndexOfAny(['.', 'e', 'E']) >= 0;
        if (!hasFractionOrExponent)
        {
            if (int.TryParse(
                    normalizedNumber,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var integer))
            {
                value = integer;
                return true;
            }

            if (long.TryParse(
                    normalizedNumber,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var longInteger))
            {
                value = longInteger;
                return true;
            }

            if (ulong.TryParse(
                    normalizedNumber,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var unsignedInteger))
            {
                value = unsignedInteger;
                return true;
            }
        }

        if (decimal.TryParse(
                normalizedNumber,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var decimalNumber) &&
            HasSameCanonicalValue(normalizedNumber, decimalNumber))
        {
            value = decimalNumber;
            return true;
        }

        value = null;
        return false;
    }

    private static bool HasSameCanonicalValue(string sourceNumber, decimal candidate)
    {
        var candidateNumber = JsonSerializer.Serialize(candidate);
        return string.Equals(
            CanonicalJsonNumber.Normalize(sourceNumber),
            CanonicalJsonNumber.Normalize(candidateNumber),
            StringComparison.Ordinal);
    }

    private static JsonElement CreateJsonNumber(string normalizedNumber)
    {
        using var document = JsonDocument.Parse(normalizedNumber);
        return document.RootElement.Clone();
    }

    private static bool HasImplicitOrNumericTag(Scalar scalar) =>
        scalar.Tag.IsEmpty ||
        scalar.Tag.IsNonSpecific ||
        scalar.Tag.Value is "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float";

    private static bool TryNormalizeJsonNumber(string yamlNumber, out string normalizedNumber)
    {
        if (!YamlDecimalNumberPattern().IsMatch(yamlNumber))
        {
            normalizedNumber = string.Empty;
            return false;
        }

        var numberStart = yamlNumber[0] is '+' or '-' ? 1 : 0;
        var sign = yamlNumber[0] == '-' ? "-" : string.Empty;
        var exponentMarker = yamlNumber.IndexOfAny(['e', 'E']);
        var significandEnd = exponentMarker >= 0 ? exponentMarker : yamlNumber.Length;
        var significand = yamlNumber[numberStart..significandEnd];
        var exponent = exponentMarker >= 0 ? yamlNumber[exponentMarker..] : string.Empty;

        if (significand[0] == '.')
        {
            significand = string.Concat("0", significand);
        }
        else if (significand[^1] == '.')
        {
            significand = string.Concat(significand, "0");
        }

        var decimalPoint = significand.IndexOf('.', StringComparison.Ordinal);
        var integerEnd = decimalPoint >= 0 ? decimalPoint : significand.Length;
        var integer = significand[..integerEnd].TrimStart('0');
        if (integer.Length == 0)
        {
            integer = "0";
        }

        normalizedNumber = string.Concat(
            sign,
            integer,
            significand[integerEnd..],
            exponent);
        return true;
    }

    [GeneratedRegex(
        "^[+-]?(?:(?:[0-9]+(?:\\.[0-9]*)?)|(?:\\.[0-9]+))(?:[eE][+-]?[0-9]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex YamlDecimalNumberPattern();
}
