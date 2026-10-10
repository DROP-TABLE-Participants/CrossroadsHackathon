using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;

public static class UserRoutes
{
    public static IEndpointRouteBuilder MapUserRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/users", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            return adminError is not null
                ? adminError
                : Results.Ok(await users.ListUsersAsync(runtime.Tenant!, cancellationToken));
        });

        app.MapPost("/api/runtime/users/invite", async (
            RuntimeUserInviteRequest request,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            try
            {
                var invite = await users.InviteUserAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    request.Email,
                    request.Name,
                    request.Roles,
                    request.EmployeeRecordId,
                    TimeSpan.FromDays(7),
                    cancellationToken);
                return Results.Ok(new
                {
                    invite.UserId,
                    activationUrl = invite.ActivationToken is null ? null : BuildActivationUrl(runtime.Tenant!.RuntimeBaseUrl, invite.ActivationToken, request.Email, request.Name)
                });
            }
            catch (RuntimeUserValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPatch("/api/runtime/users/{userId:long}", async (
            long userId,
            RuntimeUserUpdateRequest request,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            try
            {
                var updated = await users.UpdateUserAsync(runtime.Tenant!, runtime.User!, userId, request.Name, request.Roles, cancellationToken);
                return updated ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "Runtime user was not found." });
            }
            catch (RuntimeUserValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPost("/api/runtime/users/{userId:long}/roles", async (
            long userId,
            RuntimeUserRoleRequest request,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            try
            {
                var updated = await users.AddRoleAsync(runtime.Tenant!, runtime.User!, userId, request.Role, cancellationToken);
                return updated ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "Runtime user was not found." });
            }
            catch (RuntimeUserValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapDelete("/api/runtime/users/{userId:long}/roles/{role}", async (
            long userId,
            string role,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            try
            {
                var updated = await users.RemoveRoleAsync(runtime.Tenant!, runtime.User!, userId, role, cancellationToken);
                return updated ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "Runtime user was not found." });
            }
            catch (RuntimeUserValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPost("/api/runtime/users/{userId:long}/disable", async (
            long userId,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            var updated = await users.SetUserEnabledAsync(runtime.Tenant!, runtime.User!, userId, enabled: false, cancellationToken);
            return updated ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "Runtime user was not found." });
        });

        app.MapPost("/api/runtime/users/{userId:long}/enable", async (
            long userId,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            var updated = await users.SetUserEnabledAsync(runtime.Tenant!, runtime.User!, userId, enabled: true, cancellationToken);
            return updated ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "Runtime user was not found." });
        });

        app.MapPost("/api/runtime/users/{userId:long}/link-employee", async (
            long userId,
            RuntimeUserLinkEmployeeRequest request,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            try
            {
                var updated = await users.LinkEmployeeAsync(runtime.Tenant!, runtime.User!, userId, request.EmployeeRecordId, cancellationToken);
                return updated ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "Runtime user was not found." });
            }
            catch (RuntimeUserPermissionException)
            {
                return Results.StatusCode(403);
            }
            catch (RuntimeUserValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapDelete("/api/runtime/users/{userId:long}/link-employee", async (
            long userId,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimeUserService users,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var adminError = await RequireRuntimeAdminAsync(records, permissions, runtime, cancellationToken);
            if (adminError is not null)
            {
                return adminError;
            }

            try
            {
                var updated = await users.UnlinkEmployeeAsync(runtime.Tenant!, runtime.User!, userId, cancellationToken);
                return updated ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "Runtime user link was not found." });
            }
            catch (RuntimeUserPermissionException)
            {
                return Results.StatusCode(403);
            }
            catch (RuntimeUserValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        return app;
    }
}
