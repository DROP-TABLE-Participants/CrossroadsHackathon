using System.Text.Json;
using System.Text.Json.Nodes;

namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class ClaudeStructuredOutputSchema
{
    public static JsonNode ForProvider(JsonElement schema)
    {
        var result = JsonNode.Parse(schema.GetRawText())!;
        Adapt(result);
        return result;
    }

    private static void Adapt(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            // The provider grammar excludes maxItems. Retain it as an instruction
            // and enforce the original bound locally before accepting the result.
            if (obj["type"]?.ToString() == "array" && obj.Remove("maxItems", out var maximum))
                obj["description"] = $"{obj["description"]} Return at most {maximum} items.".Trim();
            foreach (var property in obj.ToArray()) Adapt(property.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array) Adapt(child);
        }
    }

    public static void ValidateArrayLimits(JsonElement schema, JsonElement output)
    {
        if (output.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("maxItems", out var maximum) && output.GetArrayLength() > maximum.GetInt32())
                throw new JsonException("Claude structured output exceeded the original array limit.");
            if (schema.TryGetProperty("minItems", out var minimum) && output.GetArrayLength() < minimum.GetInt32())
                throw new JsonException("Claude structured output did not meet the original array minimum.");
            if (schema.TryGetProperty("items", out var items))
                foreach (var item in output.EnumerateArray()) ValidateArrayLimits(items, item);
        }
        else if (output.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
                if (output.TryGetProperty(property.Name, out var value)) ValidateArrayLimits(property.Value, value);
        }
    }
}
