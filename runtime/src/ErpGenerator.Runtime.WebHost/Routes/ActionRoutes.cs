using static RuntimeRouteHelpers;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.WebHost.Observability;

public static class ActionRoutes
{
    public static IEndpointRouteBuilder MapActionRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/runtime/entities/{entityKey}/records/{recordId:long}/actions/{actionKey}", async (
            string entityKey,
            long recordId,
            string actionKey,
            HttpContext httpContext,
            IControlPlaneClient controlPlane,
            RuntimeUsageEventForwarder usageEvents,
            IRuntimeRecordService records,
            IRuntimeWorkflowService workflows,
            IRuntimeActionService actions,
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
                var entity = await records.GetEntityAsync(runtime.Tenant!, entityKey, cancellationToken);
                if (entity is null)
                {
                    return Results.NotFound(new { message = "Entity was not found." });
                }

                var user = runtime.User!;
                if (await workflows.HasActionAsync(runtime.Tenant!, entity.Key, actionKey, cancellationToken))
                {
                    var canRead = await permissions.IsAllowedAsync(runtime.Tenant!, user, "read", entity, cancellationToken);
                    var canStart = await permissions.IsAllowedAsync(runtime.Tenant!, user, "start_workflow", entity, cancellationToken);
                    if (!canRead || !canStart)
                    {
                        await AuditPermissionDeniedAsync(
                            records,
                            runtime,
                            canRead ? "start_workflow" : "read",
                            entity.Key,
                            cancellationToken);
                        return Results.StatusCode(403);
                    }

                    var workflowResult = await workflows.StartActionAsync(runtime.Tenant!, user, entity.Key, recordId, actionKey, cancellationToken);
                    await usageEvents.ForwardAsync(
                        runtime,
                        "action_executed",
                        entityKey: entity.Key,
                        recordId: recordId.ToString(),
                        workflowKey: workflowResult.Instance.WorkflowId,
                        actionKey: actionKey,
                        cancellationToken: cancellationToken);
                    return Results.Ok(workflowResult);
                }

                var actionResult = await actions.ExecuteActionAsync(runtime.Tenant!, user, entity.Key, recordId, actionKey, cancellationToken);
                await usageEvents.ForwardAsync(
                    runtime,
                    "action_executed",
                    entityKey: entity.Key,
                    recordId: recordId.ToString(),
                    actionKey: actionKey,
                    cancellationToken: cancellationToken);
                return Results.Ok(actionResult);
            }
            catch (RuntimeWorkflowNotFoundException)
            {
                return Results.NotFound(new { message = "Workflow action was not found." });
            }
            catch (RuntimeActionNotFoundException)
            {
                return Results.NotFound(new { message = "Action was not found." });
            }
            catch (RuntimeWorkflowRecordNotFoundException)
            {
                return Results.NotFound(new { message = "Record was not found or is archived." });
            }
            catch (RuntimeActionRecordNotFoundException)
            {
                return Results.NotFound(new { message = "Record was not found or is archived." });
            }
            catch (RuntimeWorkflowStartGuardRejectedException ex)
            {
                return Results.Conflict(new
                {
                    code = BundleAcceptanceExpectedOutcomeCodes.WorkflowStartGuard,
                    message = ex.Message
                });
            }
            catch (RuntimeWorkflowConflictException ex)
            {
                return Results.Conflict(new { message = ex.Message });
            }
            catch (RuntimeActionConflictException ex)
            {
                return Results.Conflict(new { message = ex.Message });
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
            catch (RuntimeEntityNotFoundException)
            {
                return Results.NotFound(new { message = "Entity was not found." });
            }
            catch (RuntimeWorkflowValidationException)
            {
                return Results.BadRequest(new { message = "Workflow request is invalid." });
            }
            catch (RuntimeActionValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (RuntimeActionPermissionException)
            {
                return Results.StatusCode(403);
            }
            catch (RuntimeWorkflowPermissionException)
            {
                await AuditPermissionDeniedAsync(records, runtime, "start_workflow", entityKey, cancellationToken);
                return Results.StatusCode(403);
            }
        });

        return app;
    }
}
