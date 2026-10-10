using Microsoft.AspNetCore.HttpOverrides;
using ErpGenerator.Runtime.Services;
using ErpGenerator.Runtime.WebHost.Observability;
using ErpGenerator.Runtime.WebHost.Readiness;
using ErpGenerator.Runtime.Services.DeploymentBroker;
using ErpGenerator.Runtime.WebHost.Scheduling;

var builder = WebApplication.CreateBuilder(args);

RuntimeConfigurationValidator.ValidateOrThrow(builder.Configuration, builder.Environment);

builder.Services.AddErpGeneratorRuntimeServices(builder.Configuration);
builder.Services.AddSingleton<RuntimeUsageEventForwarder>();
builder.Services.Configure<RequestCorrelationOptions>(builder.Configuration.GetSection("RequestCorrelation"));
builder.Services.Configure<RuntimeSchedulerHostedOptions>(builder.Configuration.GetSection("RuntimeScheduler"));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    // Runtime ingress is restricted to the in-cluster tunnel and control plane
    // by NetworkPolicy. Their pod addresses are dynamic, so the immediate
    // trusted proxy cannot be represented as a stable IP allowlist.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddHostedService<RuntimeSchedulerHostedService>();
builder.Services.AddHttpClient<RuntimeControlPlaneReadinessProbe>();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<RequestCorrelationMiddleware>();

app.MapHealthRoutes();
app.MapPageRoutes();
app.MapMetadataRoutes();
app.MapReportRoutes();
app.MapEntityRoutes();
app.MapAuditRoutes();
app.MapFeedbackRoutes();
app.MapAssistantRoutes();
app.MapUserRoutes();
app.MapWorkflowRoutes();
app.MapTaskRoutes();
app.MapNotificationRoutes();
app.MapActionRoutes();
app.MapDeploymentRoutes();
app.MapDeploymentBrokerRoutes();
app.MapAuthRoutes();

app.Run();

public partial class Program;
