using static RuntimePageRenderer;
using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;

public static class AuthRoutes
{
    public static IEndpointRouteBuilder MapAuthRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/guest-entry", async (HttpContext context, IControlPlaneClient controlPlane, IRuntimeDatabaseService database, IRuntimeSessionService sessions, IConfiguration configuration, CancellationToken ct) =>
        {
            var portalUrl = configuration["Portal:PublicBaseUrl"] ?? "http://localhost:3001";
            if (!Uri.TryCreate(portalUrl, UriKind.Absolute, out var portal) ||
                !string.Equals(context.Request.Headers.Origin, portal.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
                !context.Request.HasFormContentType || context.Request.ContentLength is > 4096)
                return Results.BadRequest(new { message = "Open this ERP from your Portal workspace." });
            var tenant = await ResolveTenantAsync(context, controlPlane, ct);
            if (tenant is null) return Results.NotFound();
            var form = await context.Request.ReadFormAsync(ct);
            var user = await database.ConsumeGuestEntryAsync(tenant, form["ticket"].ToString(), ct);
            if (user is null) return Results.Redirect(portalUrl.TrimEnd('/') + "/en/account?entry=retry");
            context.Response.Cookies.Append("erpgenerator_runtime_session", sessions.CreateSession(tenant.TenantStableId, tenant.Host, user.Id, user.Email, user.Role), new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps, Path = "/", MaxAge = TimeSpan.FromHours(8)
            });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Redirect("/");
        }).DisableAntiforgery();
        app.MapGet("/login", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var tenant = await ResolveTenantAsync(httpContext, controlPlane, cancellationToken);
            return tenant is null
                ? Results.Content(RenderUnknownTenant(), "text/html", statusCode: StatusCodes.Status404NotFound)
                : Results.Redirect((configuration["Portal:PublicBaseUrl"] ?? "http://localhost:3001").TrimEnd('/') + "/en/account?openHost=" + Uri.EscapeDataString(tenant.Host));
        });

        app.MapGet("/logout", (HttpContext httpContext) =>
        {
            httpContext.Response.Cookies.Delete("erpgenerator_runtime_session", new CookieOptions { Path = "/" });
            return Results.Redirect("/login");
        });

        app.MapGet("/activate", (HttpRequest request) =>
        {
            var token = request.Query["token"].ToString();
            var email = request.Query["email"].ToString();
            var name = request.Query["name"].ToString();
            return Results.Content(RenderActivationForm(token, email, name), "text/html");
        });

        app.MapPost("/api/activate", async (
            HttpContext httpContext,
            ActivateOwnerRequest request,
            IControlPlaneClient controlPlane,
            IRuntimeDatabaseService runtimeDatabase,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            CancellationToken cancellationToken) =>
        {
            var tenant = await ResolveTenantAsync(httpContext, controlPlane, cancellationToken);
            if (tenant is null)
            {
                return Results.NotFound(new { message = "Tenant host is not registered." });
            }

            if (string.IsNullOrWhiteSpace(request.Token) ||
                string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.DisplayName) ||
                string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { message = "Token, email, display name, and password are required." });
            }

            var activated = await runtimeDatabase.ActivateOwnerAsync(
                tenant,
                request.Token,
                request.Email,
                request.DisplayName,
                request.Password,
                cancellationToken);
            var ownerActivated = activated;

            if (!activated)
            {
                activated = await users.ActivateInvitedUserAsync(
                    tenant,
                    request.Token,
                    request.Email,
                    request.DisplayName,
                    request.Password,
                    cancellationToken);
            }

            if (!activated)
            {
                return Results.BadRequest(new { message = "Activation token is invalid, expired, or already consumed." });
            }

            var user = await runtimeDatabase.ValidateLoginAsync(tenant, request.Email, request.Password, cancellationToken);
            if (user is not null)
            {
                await records.RecordAuditEventAsync(
                    tenant,
                    new RuntimeUserContext(user.Id, user.Email, user.Role) { Roles = user.Roles },
                    "auth.activation_completed",
                    "Runtime owner activation completed.",
                    new Dictionary<string, object?>
                    {
                        ["email"] = user.Email
                    },
                    cancellationToken);
                if (ownerActivated)
                {
                    await records.RecordAuditEventAsync(
                        tenant,
                        new RuntimeUserContext(user.Id, user.Email, user.Role) { Roles = user.Roles },
                        "runtime_user.activated",
                        "Runtime user activated.",
                        new Dictionary<string, object?>
                        {
                            ["runtimeUserId"] = user.Id,
                            ["email"] = user.Email
                        },
                        cancellationToken);
                    try
                    {
                        await controlPlane.UpdateOwnerActivationStatusAsync(
                            new OwnerActivationStatusUpdate(
                                tenant.Host,
                                tenant.Environment.ToString().ToLowerInvariant(),
                                user.Email,
                                "activated",
                                DateTimeOffset.UtcNow),
                            cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        httpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("RuntimeOwnerActivation")
                            .LogWarning(
                                exception,
                                "Runtime owner activation completed, but control-plane activation status sync failed for host {Host}.",
                                tenant.Host);
                    }
                }
            }

            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/auth/login", async (
            HttpContext httpContext,
            RuntimeLoginRequest request,
            IControlPlaneClient controlPlane,
            IRuntimeDatabaseService runtimeDatabase,
            IRuntimeRecordService records,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var tenant = await ResolveTenantAsync(httpContext, controlPlane, cancellationToken);
            if (tenant is null)
            {
                return Results.NotFound(new { message = "Tenant host is not registered." });
            }

            var user = await runtimeDatabase.ValidateLoginAsync(tenant, request.Email, request.Password, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            await records.RecordAuditEventAsync(
                tenant,
                new RuntimeUserContext(user.Id, user.Email, user.Role) { Roles = user.Roles },
                "auth.login",
                "Runtime user logged in.",
                new Dictionary<string, object?>
                {
                    ["email"] = user.Email
                },
                cancellationToken);

            var session = sessions.CreateSession(tenant.TenantStableId, tenant.Host, user.Id, user.Email, user.Role);
            httpContext.Response.Cookies.Append("erpgenerator_runtime_session", session, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = httpContext.Request.IsHttps,
                Path = "/"
            });

            return Results.Ok(new
            {
                user = new
                {
                    user.Id,
                    user.Email,
                    user.DisplayName,
                    user.Role,
                    user.Roles
                }
            });
        });

        app.MapGet("/api/auth/me", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeUserService users,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var tenant = await ResolveTenantAsync(httpContext, controlPlane, cancellationToken);
            if (tenant is null)
            {
                return Results.NotFound(new { message = "Tenant host is not registered." });
            }

            var cookie = httpContext.Request.Cookies["erpgenerator_runtime_session"];
            var session = sessions.ValidateSession(cookie, tenant.TenantStableId, tenant.Host);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            var user = await users.GetActiveUserContextAsync(tenant, session.UserId, cancellationToken);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(new
                {
                    session.TenantStableId,
                    session.Host,
                    user.UserId,
                    user.Email,
                    user.Role,
                    user.Roles,
                    session.ExpiresAt
                });
        });

        return app;
    }
}
