namespace ErpGenerator.Runtime.Shared.Configuration;

public sealed class ControlPlaneOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public string InternalSecret { get; set; } = string.Empty;
}
