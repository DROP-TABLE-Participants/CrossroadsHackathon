using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Services.Runtime;

public interface IRuntimeWorkflowService
{
    Task<IReadOnlyList<RuntimeWorkflowDefinition>> ListWorkflowsAsync(ResolvedTenantEnvironment tenant, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeRecordAction>> ListRecordActionsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, long recordId, CancellationToken cancellationToken = default);
    Task<bool> HasActionAsync(ResolvedTenantEnvironment tenant, string entityKey, string actionKey, CancellationToken cancellationToken = default);
    Task<RuntimeWorkflowStartResult> StartActionAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, long recordId, string actionKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeWorkflowInstance>> ListInstancesAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeWorkflowInstance>> ListRecordInstancesAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, string entityKey, long recordId, int limit = 100, CancellationToken cancellationToken = default);
    Task<RuntimeWorkflowInstanceDetail?> GetInstanceAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, long instanceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeWorkflowTask>> ListTasksAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, bool includeCompleted = false, CancellationToken cancellationToken = default);
    Task<RuntimeWorkflowTask?> GetTaskAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, long taskId, CancellationToken cancellationToken = default);
    Task<RuntimeWorkflowTaskRecordDetail?> GetTaskRecordAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, long taskId, CancellationToken cancellationToken = default);
    Task<RuntimeWorkflowTaskResult> ApproveTaskAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, long taskId, string? reason = null, CancellationToken cancellationToken = default);
    Task<RuntimeWorkflowTaskResult> RejectTaskAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, long taskId, string? reason = null, CancellationToken cancellationToken = default);
    Task<long> CountPendingTasksAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, CancellationToken cancellationToken = default);
    Task<long> CountUnreadNotificationsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeNotification>> ListNotificationsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, bool includeAll = false, CancellationToken cancellationToken = default);
    Task<bool> MarkNotificationReadAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, long notificationId, CancellationToken cancellationToken = default);
    Task<long> MarkAllNotificationsReadAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, CancellationToken cancellationToken = default);
    Task<long> CountRunningInstancesAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeWorkflowEvent>> ListRecentEventsAsync(ResolvedTenantEnvironment tenant, RuntimeUserContext user, int limit = 10, CancellationToken cancellationToken = default);
}
