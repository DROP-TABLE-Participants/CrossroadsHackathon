using ErpGenerator.Runtime.Shared.Bundles;

namespace ErpGenerator.Runtime.Shared.Compilation.Validation;

internal sealed class BundleValidationContext(ErpBundle bundle)
{
    public ErpBundle Bundle { get; } = bundle;

    public List<string> Errors { get; } = [];

    public HashSet<string> EntityIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, ErpField>> FieldsByEntity { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> RoleIds { get; } = new(
        BundleCompilationRules.EffectiveRoles(bundle)
        .Keys
        .Select(BundleCompilationRules.NormalizeRoleId),
        StringComparer.OrdinalIgnoreCase);
}
