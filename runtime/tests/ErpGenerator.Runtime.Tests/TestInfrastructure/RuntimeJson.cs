using System.Text.Json;

namespace ErpGenerator.Runtime.Tests;

internal static class RuntimeJson
{
    public static Dictionary<string, JsonElement> JsonObject(params (string Key, object? Value)[] values)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(values.ToDictionary(value => value.Key, value => value.Value)));
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }
}
