namespace ErpGenerator.Runtime.WebHost.Observability;

public sealed class RequestCorrelationOptions
{
    public const string DefaultHeaderName = "X-Request-ID";

    public string HeaderName { get; init; } = DefaultHeaderName;
}
