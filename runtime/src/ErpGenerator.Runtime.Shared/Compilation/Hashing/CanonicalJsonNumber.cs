using System.Globalization;
using System.Numerics;

namespace ErpGenerator.Runtime.Shared.Compilation.Hashing;

internal static class CanonicalJsonNumber
{
    public static string Normalize(string rawNumber)
    {
        var numberStart = rawNumber[0] == '-' ? 1 : 0;
        var isNegative = numberStart == 1;
        var exponentMarker = rawNumber.IndexOfAny(['e', 'E']);
        var significandEnd = exponentMarker >= 0 ? exponentMarker : rawNumber.Length;
        var significand = rawNumber[numberStart..significandEnd];
        var decimalPoint = significand.IndexOf('.', StringComparison.Ordinal);
        var fractionalDigitCount = decimalPoint >= 0 ? significand.Length - decimalPoint - 1 : 0;
        var digits = decimalPoint >= 0
            ? significand.Replace(".", string.Empty, StringComparison.Ordinal)
            : significand;

        var firstSignificantDigit = 0;
        while (firstSignificantDigit < digits.Length && digits[firstSignificantDigit] == '0')
        {
            firstSignificantDigit++;
        }

        if (firstSignificantDigit == digits.Length)
        {
            return "0";
        }

        var lastSignificantDigit = digits.Length - 1;
        while (digits[lastSignificantDigit] == '0')
        {
            lastSignificantDigit--;
        }

        var trailingZeroCount = digits.Length - lastSignificantDigit - 1;
        var explicitExponent = exponentMarker >= 0
            ? BigInteger.Parse(
                rawNumber[(exponentMarker + 1)..],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture)
            : BigInteger.Zero;
        var canonicalExponent = explicitExponent - fractionalDigitCount + trailingZeroCount;
        var canonicalDigits = digits[firstSignificantDigit..(lastSignificantDigit + 1)];

        return string.Concat(
            isNegative ? "-" : string.Empty,
            canonicalDigits,
            "e",
            canonicalExponent.ToString(CultureInfo.InvariantCulture));
    }
}
