namespace ErpGenerator.Api.WebHost.Infrastructure;

public sealed class PortalGatewayOptions
{
    public const string SectionName = "PortalGateway";
    public string Secret { get; set; } = string.Empty;
}
