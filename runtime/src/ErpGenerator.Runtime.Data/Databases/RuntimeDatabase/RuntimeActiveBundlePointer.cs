namespace ErpGenerator.Runtime.Data.Databases;

internal sealed record RuntimeActiveBundlePointer(
    long BundleVersionId,
    long? PreviousBundleVersionId,
    string BundleId,
    string BundleVersion,
    string BundleHash);
