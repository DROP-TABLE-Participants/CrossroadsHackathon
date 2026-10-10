using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErpGenerator.Runtime.Shared.Bundles;

namespace ErpGenerator.Runtime.Shared.Compilation.Hashing;

internal static class BundleHashCalculator
{
    public static string ComputeHash(
        ErpBundle bundle,
        IReadOnlyList<ScheduleDefinition>? schedules = null)
    {
        if (bundle.Schedules.Count > 0 && schedules is null)
        {
            throw new InvalidOperationException("Compiled schedule definitions are required to hash a bundle that declares schedules.");
        }

        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions
        {
            WriteIndented = false
        });
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer, omitSchedules: true);
        }

        if (schedules is { Count: > 0 })
        {
            var marker = Encoding.UTF8.GetBytes("\nerpgenerator:compiled-schedules:v1\n");
            stream.Write(marker);
            foreach (var schedule in schedules.OrderBy(schedule => schedule.Id, StringComparer.Ordinal))
            {
                var binding = Encoding.UTF8.GetBytes($"{schedule.Id}:{schedule.DefinitionHash}\n");
                stream.Write(binding);
            }
        }

        var bytes = SHA256.HashData(stream.ToArray());
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void WriteCanonical(
        JsonElement element,
        Utf8JsonWriter writer,
        bool omitSchedules = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (omitSchedules && property.NameEquals(nameof(ErpBundle.Schedules)))
                    {
                        continue;
                    }

                    if (IsEmptyOptInAiNativeMember(property))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(CanonicalJsonNumber.Normalize(element.GetRawText()), skipInputValidation: true);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsEmptyOptInAiNativeMember(JsonProperty property) =>
        property.NameEquals(nameof(ErpBundle.ExceptionRules)) &&
        property.Value.ValueKind == JsonValueKind.Array &&
        property.Value.GetArrayLength() == 0 ||
        property.NameEquals(nameof(ErpEntity.Collections)) &&
        property.Value.ValueKind == JsonValueKind.Array &&
        property.Value.GetArrayLength() == 0 ||
        property.NameEquals(nameof(ErpField.Relationship)) &&
        property.Value.ValueKind == JsonValueKind.Null;

}
