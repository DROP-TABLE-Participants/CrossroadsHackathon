using System.Security.Cryptography;
using System.Text.Json;

namespace ErpGenerator.Runtime.Shared.Compilation.Hashing;

public static class ScheduleDefinitionHashCalculator
{
    public static string Compute(
        string id,
        string entity,
        string dueField,
        IReadOnlyList<ScheduleFilterDefinition> filters,
        string workflow)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("entity", entity);
            writer.WriteString("due_field", dueField);
            writer.WritePropertyName("filters");
            writer.WriteStartArray();
            foreach (var filter in filters)
            {
                writer.WriteStartObject();
                writer.WriteString("field", filter.Field);
                writer.WriteString("op", filter.Op);
                if (filter.Value is not null)
                {
                    writer.WritePropertyName("value");
                    WriteLiteral(writer, filter.Value);
                }

                writer.WritePropertyName("values");
                writer.WriteStartArray();
                foreach (var value in filter.Values)
                {
                    WriteLiteral(writer, value);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("workflow", workflow);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static string LiteralSortKey(TypedLiteralDefinition? literal) =>
        literal is null ? string.Empty : $"{literal.Type}\u001f{literal.Value}";

    private static void WriteLiteral(Utf8JsonWriter writer, TypedLiteralDefinition literal)
    {
        writer.WriteStartObject();
        writer.WriteString("type", literal.Type);
        writer.WriteString("value", literal.Value);
        writer.WriteEndObject();
    }
}
