namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class ConnectorEvidenceReadiness
{
    internal const string SetupPending = "connector_setup_pending";
    internal const string ScopeRequired = "connector_scope_required";
    internal const string Synchronizing = "connector_syncing";
    internal const string SyncFailed = "connector_sync_failed";
    internal const string ReconnectRequired = "connector_reconnect_required";
    internal const string Expired = "connector_expired";

    internal static string? BlockingReason(
        IEnumerable<GenerationProjectInputSource> sources,
        DateTimeOffset? now = null)
    {
        var values = sources.ToArray();
        var current = now ?? DateTimeOffset.UtcNow;

        if (values.Any(value =>
                value.ProviderConfigKey == "notion" && value.ScopeMode == "unconfigured" ||
                value.Status is "awaiting_scope" or "awaiting_selection"))
            return ScopeRequired;

        if (values.Any(value =>
                value.Status is "requires_reconnect" or "connection_failed"))
            return ReconnectRequired;

        if (values.Any(value => value.Status is "sync_failed" or "error"))
            return SyncFailed;

        if (values.Any(value =>
                value.Status == "expired" ||
                value.RetainContentUntil is not null && value.RetainContentUntil <= current))
            return Expired;

        if (values.Any(value =>
                value.Status is "draft" or "connecting" or "authenticated" or "connected"))
            return SetupPending;

        return values.Any(value => value.Status == "syncing")
            ? Synchronizing
            : null;
    }

    internal static bool IsSynchronizing(IEnumerable<GenerationProjectInputSource> sources)
    {
        var reason = BlockingReason(sources);
        return reason is SetupPending or Synchronizing;
    }
}
