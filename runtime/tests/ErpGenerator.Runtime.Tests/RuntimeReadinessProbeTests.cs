using System.Net;
using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.WebHost.Readiness;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeReadinessProbeTests
{
    [Fact]
    public async Task Production_readiness_requires_the_control_plane_database_readiness_endpoint()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var probe = Probe(handler, "https://control-plane.test", "Production");

        var result = await probe.CheckAsync();

        result.Should().Be(new RuntimeControlPlaneReadinessResult(true, "ok"));
        handler.RequestUri.Should().Be(new Uri("https://control-plane.test/api/ready"));
        handler.Authorization.Should().BeNull("readiness must not transmit service credentials");
    }

    [Fact]
    public async Task Production_readiness_fails_closed_without_leaking_provider_errors()
    {
        var probe = Probe(new RecordingHandler(HttpStatusCode.ServiceUnavailable),
            "https://secret-user:secret-password@control-plane.test",
            "Production");

        var result = await probe.CheckAsync();

        result.Ready.Should().BeFalse();
        result.Status.Should().Be("unavailable");
        result.Status.Should().NotContain("secret-user").And.NotContain("secret-password");
    }

    [Fact]
    public async Task Development_readiness_is_network_independent()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        var probe = Probe(handler, string.Empty, "Development");

        var result = await probe.CheckAsync();

        result.Should().Be(new RuntimeControlPlaneReadinessResult(true, "skipped_non_production"));
        handler.RequestUri.Should().BeNull();
    }

    private static RuntimeControlPlaneReadinessProbe Probe(
        RecordingHandler handler,
        string baseUrl,
        string environment) =>
        new(
            new HttpClient(handler),
            Options.Create(new ControlPlaneOptions { BaseUrl = baseUrl }),
            new TestEnvironment(environment));

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "ErpGenerator.Runtime.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
