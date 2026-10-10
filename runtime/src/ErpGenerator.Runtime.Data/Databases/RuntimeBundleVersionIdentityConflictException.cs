namespace ErpGenerator.Runtime.Data.Databases;

internal sealed class RuntimeBundleVersionIdentityConflictException : InvalidOperationException
{
    public RuntimeBundleVersionIdentityConflictException(
        string bundleId,
        string version,
        string candidateHash,
        string? existingHash = null)
        : base(existingHash is null
            ? $"Bundle '{bundleId}' version '{version}' was not recorded with candidate hash '{candidateHash}'."
            : $"Bundle '{bundleId}' version '{version}' is immutable and already belongs to hash '{existingHash}', not candidate hash '{candidateHash}'.")
    {
    }
}
