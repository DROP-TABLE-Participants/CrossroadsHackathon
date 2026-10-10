using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using ErpGenerator.Api.Services;
using ErpGenerator.Api.Services.Contracts.Realtime;
using ErpGenerator.Api.Services.Options.Identity;
using ErpGenerator.Api.WebHost;
using ErpGenerator.Api.WebHost.Infrastructure;
using ErpGenerator.Api.WebHost.Realtime;

var builder = WebApplication.CreateBuilder(args);

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtOptions = jwtSection.Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) || Encoding.UTF8.GetByteCount(jwtOptions.SigningKey) < 32)
{
    throw new InvalidOperationException("Jwt__SigningKey must contain at least 32 bytes.");
}
var adminJwtSection = builder.Configuration.GetSection(AdminJwtOptions.SectionName);
var adminJwtOptions = adminJwtSection.Get<AdminJwtOptions>() ?? new AdminJwtOptions();
if (string.IsNullOrWhiteSpace(adminJwtOptions.SigningKey) ||
    Encoding.UTF8.GetByteCount(adminJwtOptions.SigningKey) < 32)
{
    throw new InvalidOperationException("AdminJwt__SigningKey must contain at least 32 bytes.");
}

builder.Services.Configure<JwtOptions>(jwtSection);
builder.Services.Configure<AdminJwtOptions>(adminJwtSection);
builder.Services.Configure<PortalGatewayOptions>(builder.Configuration.GetSection(PortalGatewayOptions.SectionName));
builder.Services.AddSingleton<IProjectRealtimeEventSink, SignalRProjectRealtimeEventSink>();
builder.Services.AddErpGeneratorServices(builder.Configuration);
builder.Services.AddHostedService<GenerationWorker>();
builder.Services.AddHostedService<RuntimeDeploymentWorker>();
builder.Services.AddHostedService<DemoPreparationWorker>();
builder.Services.AddHostedService<NotificationDeliveryWorker>();
builder.Services.AddHostedService<ConnectorRetentionWorker>();
builder.Services.AddControllers();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false;
    options.MaximumReceiveMessageSize = 4 * 1024;
});
builder.Services.AddOpenApi();
static string ClientKey(HttpContext context)
{
    var forwarded = context.Request.Headers["x-erpgenerator-client-ip"].ToString().Trim();
    return !string.IsNullOrWhiteSpace(forwarded)
        ? forwarded
        : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("guest-entry", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true, PermitLimit = 60, QueueLimit = 0, Window = TimeSpan.FromMinutes(1)
        }));
    options.AddPolicy("public-interviews", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Request.RouteValues["token"]?.ToString() ?? "missing-token",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 60,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 10,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1)
        }));
    options.AddPolicy("public-read", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 120,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1)
        }));
    options.AddPolicy("demo-booking", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 5,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(10)
        }));
    options.AddPolicy("nango", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 20,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1)
        }));
    options.AddPolicy("invites", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 20,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(10)
        }));
    options.AddPolicy("generation-start", context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 10,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(10)
        }));
    options.AddPolicy("admin", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 120,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1)
        }));
});
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = AuthenticationSchemes.CustomerBearer;
        options.DefaultChallengeScheme = AuthenticationSchemes.CustomerBearer;
    })
    .AddJwtBearer(AuthenticationSchemes.CustomerBearer, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.HttpContext.Request.Path.StartsWithSegments(
                        "/realtime/hubs/customer",
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = context.Request.Cookies["erpgenerator-customer-access"];
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var validator = context.HttpContext.RequestServices.GetRequiredService<IIdentityAccountValidator>();
                if (context.Principal is null ||
                    !await validator.IsActiveCustomerAsync(
                        context.Principal,
                        context.HttpContext.RequestAborted))
                {
                    context.Fail("Customer account is not active.");
                }
            }
        };
    })
    .AddJwtBearer(AuthenticationSchemes.AdminBearer, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = adminJwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = adminJwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(adminJwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.HttpContext.Request.Path.StartsWithSegments(
                        "/realtime/hubs/admin",
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = context.Request.Cookies["erpgenerator-admin-access"];
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var validator = context.HttpContext.RequestServices.GetRequiredService<IIdentityAccountValidator>();
                if (context.Principal is null ||
                    !await validator.IsActiveAdministratorAsync(
                        context.Principal,
                        context.HttpContext.RequestAborted))
                {
                    context.Fail("Administrator account is not active.");
                }
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.CustomerPortalUser, policy =>
    {
        policy.AddAuthenticationSchemes(AuthenticationSchemes.CustomerBearer);
        policy.RequireClaim(IdentityClaimNames.AccountRealm, IdentityClaimNames.CustomerRealm);
        policy.RequireAuthenticatedUser();
    });
    options.AddPolicy(AuthorizationPolicies.PlatformAdministrator, policy =>
    {
        policy.AddAuthenticationSchemes(AuthenticationSchemes.AdminBearer);
        policy.RequireClaim(IdentityClaimNames.AccountRealm, IdentityClaimNames.AdministratorRealm);
        policy.RequireRole(IdentityRoleNames.PlatformAdministrator);
        policy.RequireAuthenticatedUser();
    });
});

var app = builder.Build();

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<InternalApiAuthenticationMiddleware>();
app.UseMiddleware<NangoWebhookAuthenticationMiddleware>();
app.UseMiddleware<PortalGatewayAuthenticationMiddleware>();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/ready", () => Results.Ok(new { status = "ready" }));
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/ready", () => Results.Ok(new { status = "ready" }));
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapControllers();
app.MapHub<CustomerRealtimeHub>(
    "/realtime/hubs/customer",
    options => options.CloseOnAuthenticationExpiration = true);
app.MapHub<AdminRealtimeHub>(
    "/realtime/hubs/admin",
    options => options.CloseOnAuthenticationExpiration = true);

app.Run();

public partial class Program;
