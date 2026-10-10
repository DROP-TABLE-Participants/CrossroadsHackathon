using System.Security.Cryptography;
using System.Text.Json;

namespace ErpGenerator.Runtime.Shared.Bundles;

/// <summary>
/// Canonical identity for the exact seed fixture consumed by a v3 acceptance
/// contract. Paths and YAML formatting are excluded; every executable value
/// and record order is bound.
/// </summary>
public static class BundleSeedContractHasher
{
    private const string Schema = "erpgenerator.bundle-seed.v1";

    public static string ComputeHash(BundleSeedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Seed is null || document.Records is null)
        {
            throw new BundleAcceptanceTestContractException("Acceptance seed contract is malformed.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WritePropertyName("seed");
            writer.WriteStartObject();
            writer.WriteString("key", document.Seed.Key);
            writer.WriteString("label", document.Seed.Label);
            writer.WriteEndObject();
            writer.WritePropertyName("records");
            writer.WriteStartArray();
            foreach (var record in document.Records)
            {
                if (record is null)
                {
                    throw new BundleAcceptanceTestContractException("Acceptance seed contains a malformed null record.");
                }

                writer.WriteStartObject();
                writer.WriteString("ref", record.Ref);
                writer.WriteString("entity", record.Entity);
                BundleAcceptanceCanonicalJson.WriteDictionary(writer, "values", record.Values);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
