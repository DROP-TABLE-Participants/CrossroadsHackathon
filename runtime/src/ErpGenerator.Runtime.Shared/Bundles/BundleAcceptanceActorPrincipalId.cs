using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ErpGenerator.Runtime.Shared.Bundles;

/// <summary>
/// Canonical deterministic principal id for a v3 acceptance actor. The id is
/// deliberately negative and restricted to the signed 32-bit range so it can
/// be used by both runtime identity rows and actor-principal bundle fields
/// without colliding with database-generated positive user ids.
/// </summary>
public static class BundleAcceptanceActorPrincipalId
{
    public static long Derive(string suiteHash, string testKey, string actorId)
    {
        ArgumentNullException.ThrowIfNull(suiteHash);
        ArgumentNullException.ThrowIfNull(testKey);
        ArgumentNullException.ThrowIfNull(actorId);

        if (!IsSha256(suiteHash))
        {
            throw new ArgumentException(
                "V3 acceptance actor principals require an exact lowercase SHA-256 suite hash.",
                nameof(suiteHash));
        }

        RequireExactComponent(testKey, nameof(testKey));
        RequireExactComponent(actorId, nameof(actorId));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{suiteHash}:{testKey}:{actorId}"));
        var positive = (long)(BinaryPrimitives.ReadUInt64LittleEndian(hash) & int.MaxValue);
        return -Math.Max(1L, positive);
    }

    private static void RequireExactComponent(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "V3 acceptance actor principal components must be non-empty exact values without surrounding whitespace.",
                parameterName);
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
