using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public sealed partial class RuntimeActionStore
{
    private sealed class RuntimeActionContext(
        RuntimeEntityDefinition sourceEntity,
        IReadOnlyDictionary<string, object?> sourceRecord)
    {
        public RuntimeEntityDefinition SourceEntity { get; } = sourceEntity;
        public IReadOnlyDictionary<string, object?> SourceRecord { get; private set; } = sourceRecord;
        public Dictionary<string, RuntimeActionQueryResult> QueryResults { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RuntimeActionCreated> CreatedRecords { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RuntimeActionCreatedSet> CreatedRecordSets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Counts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ActionReturnDefinition? Return { get; set; }

        public void ReplaceSourceRecord(IReadOnlyDictionary<string, object?> record) => SourceRecord = record;
    }

    private sealed record RuntimeActionQueryResult(
        RuntimeEntityDefinition Entity,
        IReadOnlyList<Dictionary<string, object?>> Records,
        bool HasHiddenMatches);

    private sealed record RuntimeActionCreated(
        RuntimeEntityDefinition Entity,
        Dictionary<string, object?> Record);

    private sealed record RuntimeActionCreatedSet(
        RuntimeEntityDefinition Entity,
        IReadOnlyList<Dictionary<string, object?>> Records);

    private enum RuntimeActionQueryLockMode
    {
        None,
        Share,
        Update
    }
}

public sealed record RuntimeActionExecutionResult(
    bool Success,
    string Action,
    string Message,
    RuntimeActionCreatedRecord? Created,
    IReadOnlyDictionary<string, int> Counts);

public sealed record RuntimeActionCreatedRecord(
    string Entity,
    string EntityId,
    long Id,
    string Url);

public sealed class RuntimeActionNotFoundException(string key) : Exception($"Action '{key}' was not found.");

public sealed class RuntimeActionRecordNotFoundException(string entityKey, long recordId) : Exception($"Record '{entityKey}/{recordId}' was not found.");

public sealed class RuntimeActionPermissionException : Exception
{
    public RuntimeActionPermissionException(string message)
        : this(string.Empty, string.Empty, message)
    {
    }

    public RuntimeActionPermissionException(
        string permissionAction,
        string target,
        string message)
        : base(message)
    {
        PermissionAction = permissionAction;
        Target = target;
    }

    /// <summary>
    /// A runtime operation such as named_action.execute, create, read, or update.
    /// Empty for legacy or otherwise unclassified denials, which must not be
    /// accepted as proof of a typed expected outcome.
    /// </summary>
    public string PermissionAction { get; }

    public string Target { get; }
}

public class RuntimeActionConflictException(string message) : Exception(message);

public sealed class RuntimeActionNotAvailableException : RuntimeActionConflictException
{
    public RuntimeActionNotAvailableException(string actionKey)
        : base("Action is not available for the current record state.")
    {
        ActionKey = actionKey;
    }

    public string ActionKey { get; }
}

public class RuntimeActionValidationException(string message) : Exception(message);

public sealed class RuntimeActionDefinitionException : RuntimeActionValidationException
{
    public RuntimeActionDefinitionException(string actionKey, string reason)
        : base($"Action '{actionKey}' has an invalid runtime definition: {reason}")
    {
        ActionKey = actionKey;
        Reason = reason;
    }

    public string ActionKey { get; }

    public string Reason { get; }
}

public sealed class RuntimeActionGuardRejectedException : RuntimeActionValidationException
{
    public RuntimeActionGuardRejectedException(
        string actionKey,
        string guardStepId,
        string message)
        : base(message)
    {
        ActionKey = actionKey;
        GuardStepId = guardStepId;
    }

    public string ActionKey { get; }

    public string GuardStepId { get; }
}
