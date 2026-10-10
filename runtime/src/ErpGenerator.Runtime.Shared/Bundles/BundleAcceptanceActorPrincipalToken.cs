using System.Collections;
using System.Text.Json;

namespace ErpGenerator.Runtime.Shared.Bundles;

/// <summary>
/// Canonical v3 value token for the deterministic principal assigned to an
/// acceptance actor. Tokens remain symbolic in the hashed contract and are
/// resolved only inside that test's actor context.
/// </summary>
public static class BundleAcceptanceActorPrincipalToken
{
    public const string Prefix = "$actor:";

    public static string For(string actorId) => $"{Prefix}{actorId}";

    public static bool IsCandidate(string? value) =>
        value?.StartsWith("$actor", StringComparison.OrdinalIgnoreCase) == true;

    public static bool TryParse(string? value, out string actorId)
    {
        actorId = string.Empty;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = value[Prefix.Length..];
        if (!IsStableId(candidate))
        {
            return false;
        }

        actorId = candidate;
        return true;
    }

    internal static IEnumerable<string> CandidatesIn(object? value)
    {
        if (value is string text)
        {
            if (IsCandidate(text))
            {
                yield return text;
            }

            yield break;
        }

        if (value is JsonElement element)
        {
            foreach (var candidate in CandidatesIn(element))
            {
                yield return candidate;
            }

            yield break;
        }

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                foreach (var candidate in CandidatesIn(entry.Value))
                {
                    yield return candidate;
                }
            }

            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                foreach (var candidate in CandidatesIn(item))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> CandidatesIn(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (IsCandidate(text))
                {
                    yield return text!;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var candidate in CandidatesIn(item))
                    {
                        yield return candidate;
                    }
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var candidate in CandidatesIn(property.Value))
                    {
                        yield return candidate;
                    }
                }
                break;
        }
    }

    private static bool IsStableId(string value) =>
        value.Length is > 0 and <= 128 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.');
}
