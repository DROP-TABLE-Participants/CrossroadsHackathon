using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Shared.Bundles;

/// <summary>
/// Binds v3 acceptance actors to the exact business-role ids emitted by the
/// compiled bundle. Grant count is deliberately not part of admission: a
/// declared role with no grants is a valid actor for negative permission tests.
/// </summary>
public static class BundleAcceptanceActorRolePolicy
{
    public static string? FindUnsupportedReason(
        IReadOnlyList<BundleAcceptanceTestDocument> tests,
        IEnumerable<string> emittedRoleIds)
    {
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentNullException.ThrowIfNull(emittedRoleIds);

        var emitted = emittedRoleIds.ToHashSet(StringComparer.Ordinal);
        foreach (var test in tests.Where(test =>
                     string.Equals(test.Schema, BundleAcceptanceTestSchemas.V3, StringComparison.Ordinal)))
        {
            foreach (var actor in test.Actors)
            {
                foreach (var role in actor.Roles)
                {
                    if (RuntimeSystemRoleIds.IsReserved(role))
                    {
                        return $"Acceptance test '{test.Test.Key}' actor '{actor.Id}' cannot use reserved system role '{role}'.";
                    }

                    if (!emitted.Contains(role))
                    {
                        return $"Acceptance test '{test.Test.Key}' actor '{actor.Id}' references role '{role}' that is not emitted by the compiled bundle.";
                    }
                }
            }
        }

        return null;
    }
}
