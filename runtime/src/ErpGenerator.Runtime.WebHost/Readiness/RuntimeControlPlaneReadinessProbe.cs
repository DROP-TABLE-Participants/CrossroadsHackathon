using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Services.ControlPlane;

namespace ErpGenerator.Runtime.WebHost.Readiness;

public sealed class RuntimeControlPlaneReadinessProbe(
    HttpClient httpClient,
    IOptions<ControlPlaneOptions> options,
    IHostEnvironment environment)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<RuntimeControlPlaneReadinessResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        if (!environment.IsProduction())
        {
            return new RuntimeControlPlaneReadinessResult(true, "skipped_non_production");
        }

        if (!Uri.TryCreate(options.Value.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            return new RuntimeControlPlaneReadinessResult(false, "invalid_configuration");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseUri, "/api/ready"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            return response.IsSuccessStatusCode
                ? new RuntimeControlPlaneReadinessResult(true, "ok")
                : new RuntimeControlPlaneReadinessResult(false, "unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeControlPlaneReadinessResult(false, "timeout");
        }
        catch (HttpRequestException)
        {
            return new RuntimeControlPlaneReadinessResult(false, "unavailable");
        }
    }
}

public sealed record RuntimeControlPlaneReadinessResult(bool Ready, string Status);
