using System.Globalization;
using ErpGenerator.Runtime.Shared.Compilation;
using System.Net;

internal static class RuntimeHtml
{
    public static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    public static string FormatInputValue(object? value) => value switch
    {
        null => string.Empty,
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    // Display formatter — US locale (M/D/YYYY, h:mm AM/PM). A value with a
    // midnight time is treated as a date-only field so it reads "7/17/2026"
    // rather than "7/17/2026, 12:00 AM". Input values (for <input type=date>)
    // stay ISO via FormatInputValue, as HTML requires.
    public static string FormatRecordValue(object? value) => value switch
    {
        null => "",
        DateTime dateTime => FormatUsDateTime(dateTime, dateTime.TimeOfDay != TimeSpan.Zero),
        DateTimeOffset dateTimeOffset => FormatUsDateTime(dateTimeOffset.DateTime, dateTimeOffset.TimeOfDay != TimeSpan.Zero),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    public static string FormatUsTimestamp(DateTimeOffset value) => FormatUsDateTime(value.DateTime, includeTime: true);

    private static string FormatUsDateTime(DateTime value, bool includeTime) =>
        includeTime ? value.ToString("M/d/yyyy, h:mm tt", Us) : value.ToString("M/d/yyyy", Us);

    public static string FormatMetricValue(object? value) => value switch
    {
        null => "0",
        decimal number when decimal.Truncate(number) == number => number.ToString("#,##0", CultureInfo.InvariantCulture),
        decimal number => number.ToString("#,##0.##", CultureInfo.InvariantCulture),
        double number => number.ToString("#,##0.##", CultureInfo.InvariantCulture),
        float number => number.ToString("#,##0.##", CultureInfo.InvariantCulture),
        long number => number.ToString("#,##0", CultureInfo.InvariantCulture),
        int number => number.ToString("#,##0", CultureInfo.InvariantCulture),
        _ => FormatRecordValue(value)
    };

    public static string FormatFieldValue(RuntimeFieldDefinition field, IReadOnlyDictionary<string, object?> record)
    {
        if (field.FieldType.Equals("relation", StringComparison.OrdinalIgnoreCase))
        {
            return FormatRecordValue(record.GetValueOrDefault($"{field.ColumnName}__label") ?? record.GetValueOrDefault(field.ColumnName));
        }

        return FormatRecordValue(record.GetValueOrDefault(field.ColumnName));
    }
}
