using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeActionStore : IRuntimeActionService
{
    private const int MaxActionRecords = 100;
    private static readonly Regex IdentifierPattern = new("^[a-z][a-z0-9_]{1,62}$", RegexOptions.Compiled);
    private static readonly Regex TemplateTokenPattern = new(@"\{\{\s*([a-zA-Z0-9_\.]+)\s*\}\}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ITenantDatabaseConnectionFactory connectionFactory;
    private readonly RuntimePermissionService permissions;

    public RuntimeActionStore(
        ITenantDatabaseConnectionFactory connectionFactory,
        RuntimePermissionService permissions)
    {
        this.connectionFactory = connectionFactory;
        this.permissions = permissions;
    }

    public async Task<ActionDefinition?> GetActionAsync(
        ResolvedTenantEnvironment tenant,
        string entityKey,
        string actionKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        return await LoadActionAsync(connection, entity.Id, actionKey, cancellationToken);
    }

    public async Task<IReadOnlyList<RuntimeRecordAction>> ListRecordActionsAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var record = await ReadRecordByIdAsync(connection, entity, recordId, cancellationToken);
        if (record is null)
        {
            return [];
        }

        var actions = await LoadActionsForEntityAsync(connection, entity.Id, cancellationToken);
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);
        var visible = new List<RuntimeRecordAction>();
        foreach (var action in actions)
        {
            var authorization = await EvaluateActionAuthorizationAsync(
                connection,
                permissionSnapshot,
                action,
                entity,
                cancellationToken);
            if (!authorization.Allowed ||
                await FindDeniedSourceActionAsync(
                    connection,
                    transaction: null,
                    permissionSnapshot,
                    user,
                    entity,
                    record,
                    authorization.Requirements,
                    cancellationToken) is not null)
            {
                continue;
            }

            if (!await AreExecutableStepFieldsAllowedAsync(
                    connection,
                    permissionSnapshot,
                    action,
                    entity,
                    authorization.Requirements,
                    cancellationToken))
            {
                continue;
            }

            try
            {
                var visibilityContext = new RuntimeActionContext(entity, record);
                foreach (var condition in action.VisibleWhen)
                {
                    EnsureConditionSourceFieldsAllowed(
                        permissionSnapshot,
                        condition,
                        visibilityContext,
                        itemEntity: null);
                }
            }
            catch (RuntimeActionPermissionException)
            {
                continue;
            }

            if (!EvaluateVisibleWhen(action, entity, record))
            {
                continue;
            }

            visible.Add(new RuntimeRecordAction(action.Key, action.Label, string.Empty, "business_action"));
        }

        return visible;
    }

    public async Task<RuntimeActionExecutionResult> ExecuteActionAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeUserContext user,
        string entityKey,
        long recordId,
        string actionKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(tenant, cancellationToken);
        var entity = await LoadEntityAsync(connection, entityKey, cancellationToken)
                     ?? throw new RuntimeEntityNotFoundException(entityKey);
        var action = await LoadActionAsync(connection, entity.Id, actionKey, cancellationToken)
                     ?? throw new RuntimeActionNotFoundException(actionKey);
        var permissionSnapshot = await permissions.CreateSnapshotAsync(connection, user, cancellationToken);

        var authorization = await EvaluateActionAuthorizationAsync(
            connection,
            permissionSnapshot,
            action,
            entity,
            cancellationToken);
        if (!authorization.Allowed)
        {
            if (!authorization.DefinitionValid)
            {
                throw new RuntimeActionDefinitionException(action.Key, authorization.Reason);
            }

            if (IsSourceReadDenial(authorization, entity))
            {
                throw new RuntimeActionRecordNotFoundException(entity.Key, recordId);
            }

            await WriteAuditEventAsync(
                connection,
                null,
                tenant,
                user,
                "permission.denied",
                "Runtime action permission denied.",
                new Dictionary<string, object?>
                {
                    ["action"] = action.Key,
                    ["entity"] = entity.Key,
                    ["recordId"] = recordId
                },
                cancellationToken);
            throw new RuntimeActionPermissionException(
                authorization.PermissionAction,
                authorization.Target,
                authorization.Reason);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        RuntimeActionContext context;
        try
        {
            var transactionalPermissionSnapshot = await permissions.CreateSnapshotAsync(
                connection,
                transaction,
                user,
                cancellationToken);
            if (!permissions.IsNamedActionAllowed(transactionalPermissionSnapshot, entity, action.Key))
            {
                throw new RuntimeActionPermissionException(
                    "named_action.execute",
                    action.Key,
                    "Action permission denied.");
            }

            foreach (var requirement in authorization.Requirements)
            {
                if (!permissions.IsAllowed(
                        transactionalPermissionSnapshot,
                        requirement.Action,
                        requirement.Entity))
                {
                    if (IsSourceReadRequirement(requirement, entity))
                    {
                        throw new RuntimeActionRecordNotFoundException(entity.Key, recordId);
                    }

                    throw new RuntimeActionPermissionException(
                        requirement.Action,
                        requirement.Entity.Id,
                        "Action permission denied.");
                }
            }

            var sourceRecord = await ReadRecordByIdAsync(connection, entity, recordId, cancellationToken, transaction, forUpdate: true);
            if (sourceRecord is null)
            {
                throw new RuntimeActionRecordNotFoundException(entity.Key, recordId);
            }

            await EnsureSourceRecordRequirementsAllowedAsync(
                connection,
                transaction,
                transactionalPermissionSnapshot,
                user,
                entity,
                sourceRecord,
                recordId,
                authorization.Requirements,
                cancellationToken);

            var visibilityContext = new RuntimeActionContext(entity, sourceRecord);
            foreach (var condition in action.VisibleWhen)
            {
                EnsureConditionSourceFieldsAllowed(
                    transactionalPermissionSnapshot,
                    condition,
                    visibilityContext,
                    itemEntity: null);
            }

            if (!EvaluateVisibleWhen(action, entity, sourceRecord))
            {
                throw new RuntimeActionNotAvailableException(action.Key);
            }

            context = new RuntimeActionContext(entity, sourceRecord);
            await WriteAuditEventAsync(
                connection,
                transaction,
                tenant,
                user,
                "action.started",
                $"Started {action.Label}.",
                new Dictionary<string, object?>
                {
                    ["action"] = action.Key,
                    ["entity"] = entity.Key,
                    ["recordId"] = recordId
                },
                cancellationToken);

            foreach (var step in action.Steps)
            {
                await ExecuteStepAsync(
                    connection,
                    transaction,
                    tenant,
                    user,
                    transactionalPermissionSnapshot,
                    action,
                    step,
                    context,
                    authorization.Requirements,
                    cancellationToken);
            }

            var result = BuildResult(action, context);
            await WriteAuditEventAsync(
                connection,
                transaction,
                tenant,
                user,
                "action.completed",
                $"Completed {action.Label}.",
                new Dictionary<string, object?>
                {
                    ["action"] = action.Key,
                    ["entity"] = entity.Key,
                    ["recordId"] = recordId,
                    ["created"] = result.Created is null ? null : new { result.Created.Entity, result.Created.Id },
                    ["counts"] = result.Counts
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (RuntimeActionRecordNotFoundException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        catch (RuntimeActionPermissionException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (!IsFieldPermissionDenial(ex))
            {
                await WriteAuditEventAsync(
                    connection,
                    null,
                    tenant,
                    user,
                    "permission.denied",
                    "Runtime action record permission denied.",
                    new Dictionary<string, object?>
                    {
                        ["action"] = action.Key,
                        ["entity"] = entity.Key,
                        ["recordId"] = recordId
                    },
                    cancellationToken);
            }
            throw;
        }
        catch (Exception ex) when (ex is not RuntimeActionPermissionException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await WriteAuditEventAsync(
                connection,
                null,
                tenant,
                user,
                "action.failed",
                $"Failed {action.Label}.",
                new Dictionary<string, object?>
                {
                    ["action"] = action.Key,
                    ["entity"] = entity.Key,
                    ["recordId"] = recordId,
                    ["error"] = ex.Message
                },
                cancellationToken);
            throw;
        }
    }

    private static bool IsSourceReadDenial(
        RuntimeActionAuthorizationDecision authorization,
        RuntimeEntityDefinition sourceEntity) =>
        authorization.PermissionAction.Equals("read", StringComparison.OrdinalIgnoreCase) &&
        authorization.Target.Equals(sourceEntity.Id, StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceReadRequirement(
        RuntimeActionAuthorizationRequirement requirement,
        RuntimeEntityDefinition sourceEntity) =>
        requirement.Action.Equals("read", StringComparison.OrdinalIgnoreCase) &&
        requirement.Entity.Id.Equals(sourceEntity.Id, StringComparison.OrdinalIgnoreCase);
}
