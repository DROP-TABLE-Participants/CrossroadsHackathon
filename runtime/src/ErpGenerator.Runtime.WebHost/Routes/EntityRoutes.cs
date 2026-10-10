using System.Net;
using System.Text.Json;
using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.WebHost.Observability;

public static class EntityRoutes
{
    public static IEndpointRouteBuilder MapEntityRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtime/entities", async (
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var entities = await records.ListEntitiesAsync(runtime.Tenant!, cancellationToken);
            return Results.Ok(await FilterReadableEntitiesAsync(runtime.Tenant!, runtime.Session!, entities, permissions, cancellationToken));
        });

        app.MapGet("/api/runtime/entities/{entityKey}", async (
            string entityKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "read", cancellationToken);
            return authorized.Error is not null
                ? authorized.Error
                : Results.Ok(await permissions.FilterReadableEntityAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    authorized.Entity!,
                    cancellationToken));
        });

        app.MapGet("/api/runtime/entities/{entityKey}/lookup", async (
            string entityKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "read", cancellationToken);
            if (authorized.Error is not null)
            {
                return authorized.Error;
            }

            var limit = int.TryParse(httpContext.Request.Query["limit"], out var parsedLimit) ? parsedLimit : 20;
            var search = httpContext.Request.Query["q"].ToString();
            try
            {
                return Results.Ok(await records.LookupRecordsAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    authorized.Entity!.Key,
                    search,
                    limit,
                    cancellationToken));
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "read", authorized.Entity!.Key, cancellationToken);
                return Results.StatusCode(403);
            }
        });

        app.MapGet("/api/runtime/entities/{entityKey}/records", async (
            string entityKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            try
            {
                var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "read", cancellationToken);
                if (authorized.Error is not null)
                {
                    return authorized.Error;
                }

                var query = BuildRecordQuery(httpContext.Request);
                if (query.IncludeArchived &&
                    !await permissions.IsAllowedAsync(
                        runtime.Tenant!,
                        runtime.User!,
                        "restore",
                        authorized.Entity!,
                        cancellationToken))
                {
                    await AuditPermissionDeniedAsync(records, runtime, "includeArchived", entityKey, cancellationToken);
                    return Results.StatusCode(403);
                }

                var list = await records.ListRecordsAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    authorized.Entity!.Key,
                    query,
                    cancellationToken);
                return Results.Ok(await permissions.FilterReadableRecordListFieldsAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    list,
                    cancellationToken));
            }
            catch (RuntimeEntityNotFoundException)
            {
                return Results.NotFound(new { message = "Entity was not found." });
            }
            catch (RuntimeRecordValidationException ex)
            {
                return Results.BadRequest(new { message = "Validation failed.", errors = ex.Errors });
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "read", entityKey, cancellationToken);
                return Results.StatusCode(403);
            }
        });

        app.MapPost("/api/runtime/entities/{entityKey}/records", async (
            string entityKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            try
            {
                var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "create", cancellationToken);
                if (authorized.Error is not null)
                {
                    return authorized.Error;
                }

                var body = await ReadJsonObjectAsync(httpContext.Request, cancellationToken);
                if (!await permissions.AreFieldsAllowedAsync(
                        runtime.Tenant!,
                        runtime.User!,
                        "write",
                        authorized.Entity!,
                        body.Keys,
                        cancellationToken))
                {
                    await AuditPermissionDeniedAsync(records, runtime, "field:write", authorized.Entity!.Key, cancellationToken);
                    return Results.StatusCode(403);
                }

                var record = await records.CreateRecordAsync(runtime.Tenant!, runtime.User!, authorized.Entity!.Key, body, cancellationToken);
                await usageEvents.ForwardAsync(
                    runtime,
                    "entity_record_created",
                    entityKey: authorized.Entity.Key,
                    recordId: Convert.ToString(record["id"]),
                    metadata: new Dictionary<string, object?> { ["fieldCount"] = body.Count },
                    cancellationToken: cancellationToken);
                var readable = await permissions.FilterReadableRecordFieldsAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    authorized.Entity,
                    record,
                    cancellationToken);
                return Results.Created($"/api/runtime/entities/{WebUtility.UrlEncode(authorized.Entity.Key)}/records/{record["id"]}", readable);
            }
            catch (RuntimeEntityNotFoundException)
            {
                return Results.NotFound(new { message = "Entity was not found." });
            }
            catch (RuntimeRelationCreateGuardRejectedException ex)
            {
                return Results.Conflict(new
                {
                    code = ex.Code,
                    message = ex.Message,
                    entity = ex.EntityKey,
                    relationField = ex.RelationField,
                    referencedEntity = ex.ReferencedEntity,
                    referencedField = ex.ReferencedField
                });
            }
            catch (RuntimeRecordValidationException ex)
            {
                return Results.BadRequest(new { message = "Validation failed.", errors = ex.Errors });
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "create", entityKey, cancellationToken);
                return Results.StatusCode(403);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "Request body must be a JSON object." });
            }
        });

        app.MapGet("/api/runtime/entities/{entityKey}/records/{recordId:long}", async (
            string entityKey,
            long recordId,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            try
            {
                var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "read", cancellationToken);
                if (authorized.Error is not null)
                {
                    return authorized.Error;
                }

                var includeArchived = httpContext.Request.Query["includeArchived"] == "true";
                if (includeArchived &&
                    !await permissions.IsAllowedAsync(
                        runtime.Tenant!,
                        runtime.User!,
                        "restore",
                        authorized.Entity!,
                        cancellationToken))
                {
                    await AuditPermissionDeniedAsync(records, runtime, "includeArchived", entityKey, cancellationToken);
                    return Results.StatusCode(403);
                }

                var record = await records.GetRecordAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    authorized.Entity!.Key,
                    recordId,
                    includeArchived,
                    cancellationToken);
                return record is null
                    ? Results.NotFound(new { message = "Record was not found." })
                    : Results.Ok(await permissions.FilterReadableRecordFieldsAsync(
                        runtime.Tenant!,
                        runtime.User!,
                        authorized.Entity,
                        record,
                        cancellationToken));
            }
            catch (RuntimeEntityNotFoundException)
            {
                return Results.NotFound(new { message = "Entity was not found." });
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "read", entityKey, cancellationToken);
                return Results.StatusCode(403);
            }
        });

        app.MapPatch("/api/runtime/entities/{entityKey}/records/{recordId:long}", async (
            string entityKey,
            long recordId,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            try
            {
                var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "update", cancellationToken);
                if (authorized.Error is not null)
                {
                    return authorized.Error;
                }

                var body = await ReadJsonObjectAsync(httpContext.Request, cancellationToken);
                if (!await permissions.AreFieldsAllowedAsync(
                        runtime.Tenant!,
                        runtime.User!,
                        "write",
                        authorized.Entity!,
                        body.Keys,
                        cancellationToken))
                {
                    await AuditPermissionDeniedAsync(records, runtime, "field:write", authorized.Entity!.Key, cancellationToken);
                    return Results.StatusCode(403);
                }

                var record = await records.UpdateRecordAsync(runtime.Tenant!, runtime.User!, authorized.Entity!.Key, recordId, body, cancellationToken);
                if (record is not null)
                {
                    await usageEvents.ForwardAsync(
                        runtime,
                        "entity_record_updated",
                        entityKey: authorized.Entity.Key,
                        recordId: recordId.ToString(),
                        metadata: new Dictionary<string, object?> { ["fieldCount"] = body.Count },
                        cancellationToken: cancellationToken);
                }

                return record is null
                    ? Results.NotFound(new { message = "Record was not found." })
                    : Results.Ok(await permissions.FilterReadableRecordFieldsAsync(
                        runtime.Tenant!,
                        runtime.User!,
                        authorized.Entity,
                        record,
                        cancellationToken));
            }
            catch (RuntimeEntityNotFoundException)
            {
                return Results.NotFound(new { message = "Entity was not found." });
            }
            catch (RuntimeRecordValidationException ex)
            {
                return Results.BadRequest(new { message = "Validation failed.", errors = ex.Errors });
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "update", entityKey, cancellationToken);
                return Results.StatusCode(403);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "Request body must be a JSON object." });
            }
        });

        app.MapDelete("/api/runtime/entities/{entityKey}/records/{recordId:long}", async (
            string entityKey,
            long recordId,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            try
            {
                var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "archive", cancellationToken);
                if (authorized.Error is not null)
                {
                    return authorized.Error;
                }

                var archived = await records.ArchiveRecordAsync(
                    runtime.Tenant!,
                    runtime.User!,
                    authorized.Entity!.Key,
                    recordId,
                    httpContext.Request.Query["reason"].ToString(),
                    cancellationToken);
                if (archived)
                {
                    await usageEvents.ForwardAsync(
                        runtime,
                        "entity_record_archived",
                        entityKey: authorized.Entity.Key,
                        recordId: recordId.ToString(),
                        cancellationToken: cancellationToken);
                }

                return archived ? Results.NoContent() : Results.NotFound(new { message = "Record was not found." });
            }
            catch (RuntimeEntityNotFoundException)
            {
                return Results.NotFound(new { message = "Entity was not found." });
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "archive", entityKey, cancellationToken);
                return Results.StatusCode(403);
            }
        });

        app.MapPost("/api/runtime/entities/{entityKey}/records/{recordId:long}/restore", async (
            string entityKey,
            long recordId,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimePermissionService permissions,
            IRuntimeSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var runtime = await ResolveAuthenticatedRuntimeAsync(httpContext, controlPlane, sessions, cancellationToken);
            if (runtime.Error is not null)
            {
                return runtime.Error;
            }

            try
            {
                var authorized = await AuthorizeEntityAsync(runtime, records, permissions, entityKey, "restore", cancellationToken);
                if (authorized.Error is not null)
                {
                    return authorized.Error;
                }

                var restored = await records.RestoreRecordAsync(runtime.Tenant!, runtime.User!, authorized.Entity!.Key, recordId, cancellationToken);
                if (restored)
                {
                    await usageEvents.ForwardAsync(
                        runtime,
                        "entity_record_restored",
                        entityKey: authorized.Entity.Key,
                        recordId: recordId.ToString(),
                        cancellationToken: cancellationToken);
                }

                return restored ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "Record was not found or is not archived." });
            }
            catch (RuntimeEntityNotFoundException)
            {
                return Results.NotFound(new { message = "Entity was not found." });
            }
            catch (RuntimeRecordPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "restore", entityKey, cancellationToken);
                return Results.StatusCode(403);
            }
        });

        return app;
    }
}
