using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ErpGenerator.Runtime.Shared.Bundles;

public static class BundleAcceptanceEvidenceValueKinds
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string RecordRef = "record_ref";
    public const string ActorPrincipal = "actor_principal";
    public const string Null = "null";
    public const string Json = "json";

    public static bool IsKnown(string? kind) => kind is
        String or Integer or Decimal or Boolean or Date or DateTime or
        RecordRef or ActorPrincipal or Null or Json;
}

public sealed record BundleAcceptanceEvidenceDigest(
    string Schema,
    string Kind,
    string Digest);

/// <summary>
/// Produces kind-bound canonical SHA-256 evidence digests. These digests keep
/// raw business values out of acceptance evidence, but plain SHA-256 is
/// redaction and integrity evidence, not encryption or protection against
/// low-entropy value guessing.
/// </summary>
public static class BundleAcceptanceEvidenceDigestContract
{
    public const string Schema = "erpgenerator.bundle-acceptance-evidence-value.v1";

    public static BundleAcceptanceEvidenceDigest CreateInferred(object? value)
    {
        var kind = InferKind(value);
        return Create(kind, value);
    }

    public static BundleAcceptanceEvidenceDigest Create(string kind, object? value)
    {
        var normalizedKind = value is null || value is JsonElement
            {
                ValueKind: JsonValueKind.Null or JsonValueKind.Undefined
            }
            ? BundleAcceptanceEvidenceValueKinds.Null
            : BundleAcceptanceEvidenceValueKinds.IsKnown(kind)
                ? kind
                : BundleAcceptanceEvidenceValueKinds.Json;
        var canonical = CanonicalizeSafely(normalizedKind, value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("kind", normalizedKind);
            writer.WriteString("value", canonical);
            writer.WriteEndObject();
        }

        return new BundleAcceptanceEvidenceDigest(
            Schema,
            normalizedKind,
            Convert.ToHexStringLower(SHA256.HashData(stream.ToArray())));
    }

    public static string InferKind(object? value)
    {
        if (value is null || value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return BundleAcceptanceEvidenceValueKinds.Null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => BundleAcceptanceEvidenceValueKinds.String,
                JsonValueKind.Number when element.TryGetInt64(out _) => BundleAcceptanceEvidenceValueKinds.Integer,
                JsonValueKind.Number => BundleAcceptanceEvidenceValueKinds.Decimal,
                JsonValueKind.True or JsonValueKind.False => BundleAcceptanceEvidenceValueKinds.Boolean,
                _ => BundleAcceptanceEvidenceValueKinds.Json
            };
        }

        return value switch
        {
            string or char => BundleAcceptanceEvidenceValueKinds.String,
            sbyte or byte or short or ushort or int or uint or long or ulong =>
                BundleAcceptanceEvidenceValueKinds.Integer,
            decimal or float or double => BundleAcceptanceEvidenceValueKinds.Decimal,
            bool => BundleAcceptanceEvidenceValueKinds.Boolean,
            DateOnly => BundleAcceptanceEvidenceValueKinds.Date,
            DateTime or DateTimeOffset => BundleAcceptanceEvidenceValueKinds.DateTime,
            _ => BundleAcceptanceEvidenceValueKinds.Json
        };
    }

    private static string CanonicalizeSafely(string kind, object? value)
    {
        try
        {
            return Canonicalize(kind, value);
        }
        catch (Exception exception) when (exception is
                   FormatException or InvalidCastException or OverflowException or InvalidOperationException or
                   JsonException or NotSupportedException)
        {
            return CanonicalJson(value);
        }
    }

    private static string Canonicalize(string kind, object? value)
    {
        if (value is null || value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return "null";
        }

        value = ScalarValue(value);
        return kind switch
        {
            BundleAcceptanceEvidenceValueKinds.String =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            BundleAcceptanceEvidenceValueKinds.Integer or
            BundleAcceptanceEvidenceValueKinds.RecordRef or
            BundleAcceptanceEvidenceValueKinds.ActorPrincipal => CanonicalInteger(value),
            BundleAcceptanceEvidenceValueKinds.Decimal => CanonicalDecimal(value),
            BundleAcceptanceEvidenceValueKinds.Boolean => CanonicalBoolean(value),
            BundleAcceptanceEvidenceValueKinds.Date => CanonicalDate(value),
            BundleAcceptanceEvidenceValueKinds.DateTime => CanonicalDateTime(value),
            BundleAcceptanceEvidenceValueKinds.Null => "null",
            _ => CanonicalJson(value)
        };
    }

    private static object ScalarValue(object value)
    {
        if (value is not JsonElement element)
        {
            return value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element
        };
    }

    private static string CanonicalInteger(object value)
    {
        if (value is decimal number && decimal.Truncate(number) == number)
        {
            return number.ToString("0", CultureInfo.InvariantCulture);
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static string CanonicalDecimal(object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var number = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
        return number.ToString("G29", CultureInfo.InvariantCulture);
    }

    private static string CanonicalBoolean(object value)
    {
        var boolean = value is bool typed
            ? typed
            : bool.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        return boolean ? "true" : "false";
    }

    private static string CanonicalDate(object value)
    {
        var date = value switch
        {
            DateOnly typed => typed,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            DateTimeOffset dateTimeOffset => DateOnly.FromDateTime(dateTimeOffset.UtcDateTime),
            _ => DateOnly.ParseExact(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)
        };
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string CanonicalDateTime(object value)
    {
        var dateTime = value switch
        {
            DateTimeOffset typed => typed,
            DateTime typed => new DateTimeOffset(
                typed.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(typed, DateTimeKind.Utc)
                    : typed),
            _ => DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal)
        };
        return dateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static string CanonicalJson(object? value)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                BundleAcceptanceCanonicalJson.WriteValue(writer, value);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value?.GetType().FullName ?? "null";
        }
    }
}
