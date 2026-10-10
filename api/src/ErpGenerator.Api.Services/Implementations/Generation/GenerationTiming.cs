namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class GenerationTiming
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumModelCallDuration = TimeSpan.FromMinutes(25);
}
