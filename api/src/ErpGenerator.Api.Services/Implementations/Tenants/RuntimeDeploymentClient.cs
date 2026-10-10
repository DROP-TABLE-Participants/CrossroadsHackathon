using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class RuntimeDeploymentClient(
    HttpClient httpClient,
    IOptions<RuntimeBrokerOptions> options) : IRuntimeDeploymentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<CuratedRuntimeDeploymentViewModel> DeployCuratedAsync(CuratedRuntimeDeploymentInputModel input, CancellationToken cancellationToken) =>
        SendAsync<CuratedRuntimeDeploymentInputModel, CuratedRuntimeDeploymentViewModel>("/internal/deployment-broker/erp/deploy-curated", input.OperationId, input, cancellationToken);
    public Task<RuntimeGuestEntryViewModel> CreateGuestEntryAsync(RuntimeGuestEntryInputModel input, CancellationToken cancellationToken) =>
        SendAsync<RuntimeGuestEntryInputModel, RuntimeGuestEntryViewModel>("/internal/deployment-broker/guest-entry", input.OperationId, input, cancellationToken);

    public Task<CanonicalRuntimeDeploymentViewModel> DeployCanonicalAsync(
        CanonicalRuntimeDeploymentInputModel input,
        CancellationToken cancellationToken) =>
        SendAsync<CanonicalRuntimeDeploymentInputModel, CanonicalRuntimeDeploymentViewModel>(
            "/internal/deployment-broker/erp/deploy-canonical", input.OperationId, input, cancellationToken);

    public Task<RuntimeOwnerActivationViewModel> CreateActivationAsync(
        RuntimeOwnerActivationInputModel input,
        CancellationToken cancellationToken) =>
        SendAsync<RuntimeOwnerActivationInputModel, RuntimeOwnerActivationViewModel>(
            "/internal/deployment-broker/owner-activation", input.OperationId, input, cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string path,
        string operationId,
        TRequest input,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Secret))
            throw new InvalidOperationException("Runtime broker configuration is incomplete.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl.TrimEnd('/')), path));
        request.Headers.Add("x-erpgenerator-deployment-broker-secret", settings.Secret);
        request.Headers.Add("x-erpgenerator-idempotency-key", operationId);
        request.Content = JsonContent.Create(input, options: JsonOptions);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 1200)));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var result = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, timeout.Token);
        if (result is null) throw new InvalidOperationException("Runtime broker returned an empty response.");
        return result;
    }
}
