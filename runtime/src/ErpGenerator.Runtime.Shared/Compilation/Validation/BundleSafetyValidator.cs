using System.Text.Json;
using System.Text.RegularExpressions;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal static class BundleSafetyValidator
{
    private static readonly Regex SqlStatementPattern = new(
        """
        \b(?:
            (?:drop|alter|truncate)\s+(?:table|schema|database|view|index|function|procedure)\b
            |
            insert\s+into\s+["\w.]+\s*(?:\(|values\b|select\b)
            |
            update\s+["\w.]+\s+set\s+["\w.]+\s*=
            |
            delete\s+from\s+["\w.]+\s*(?:where\b|using\b|returning\b|;)
            |
            select\s+\*\s+from\s+["\w.]+
            |
            select\s+["\w.]+(?:\s*,\s*["\w.]+)+\s+from\s+["\w.]+
            |
            select\s+["\w.]+\s+from\s+["\w.]+\s*(?:where\b|join\b|order\s+by\b|group\s+by\b|limit\b|offset\b|;)
        )
        """,
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnorePatternWhitespace |
        RegexOptions.NonBacktracking);

    public static void RejectSqlLikeValue(object? value, string path, List<string> errors)
    {
        switch (value)
        {
            case string text:
                RejectSqlLikeContent(text, path, errors);
                break;
            case IEnumerable<object?> values when value is not string:
                var index = 0;
                foreach (var item in values)
                {
                    RejectSqlLikeValue(item, $"{path}[{index++}]", errors);
                }

                break;
            case JsonElement { ValueKind: JsonValueKind.Array } element:
                var jsonIndex = 0;
                foreach (var item in element.EnumerateArray())
                {
                    RejectSqlLikeValue(BundleCompilationRules.JsonElementToObject(item), $"{path}[{jsonIndex++}]", errors);
                }

                break;
        }
    }

    public static void RejectSqlLikeContent(string? value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (SqlStatementPattern.IsMatch(value))
        {
            errors.Add($"{path} contains SQL-like content. ERP Bundles must remain declarative.");
        }
    }
}
