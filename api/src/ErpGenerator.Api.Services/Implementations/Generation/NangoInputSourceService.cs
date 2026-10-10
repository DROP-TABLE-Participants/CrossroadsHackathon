using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class NangoInputSourceService(
    ErpGeneratorDbContext dbContext,
    HttpClient httpClient,
    IOptions<NangoOptions> options) : INangoInputSourceService
{
    private const string AuthorizationNoticeVersion = "connector-evidence-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyList<Connector> Connectors =
    [
        new("notion", "Notion", "files_documents", ["notion"])
    ];

    public IReadOnlyList<string> AvailableConnectorKeys => Connectors
        .Where(value => ResolveProviderConfigKey(value) is not null)
        .Select(value => value.Key).ToArray();

    public async Task<NangoConnectSessionViewModel> CreateConnectSessionAsync(
        Guid customerId,
        Guid projectId,
        CreateNangoConnectSessionInputModel input,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        if (!input.AuthorizationAccepted)
            throw new ArgumentException("Confirm the connector data notice before continuing.");

        var project = await dbContext.GenerationProjects.SingleOrDefaultAsync(
            value => value.Id == projectId && value.CustomerId == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Generation project was not found.");
        var connector = Connectors.SingleOrDefault(value => value.Key == (input.ConnectorKey?.Trim() ?? "notion"))
            ?? throw new ArgumentException("This connector is not available.");
        var providerConfigKey = ResolveProviderConfigKey(connector)
            ?? throw new ArgumentException("This connector is not available.");
        var existing = await dbContext.GenerationProjectInputSources
            .Where(value => value.GenerationProjectId == projectId && value.ProviderConfigKey == providerConfigKey &&
                            (value.Status == "connecting" ||
                             value.Status == "requires_reconnect" ||
                             value.Status == "connection_failed" ||
                             value.Status == "draft"))
            .OrderByDescending(value => value.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var source = existing ?? new GenerationProjectInputSource
        {
            GenerationProjectId = project.Id,
            ProviderConfigKey = providerConfigKey,
            ConnectionId = $"pending-{Guid.NewGuid():N}"
        };
        if (existing is null) dbContext.GenerationProjectInputSources.Add(source);
        else
        {
            await DeleteNangoConnectionAsync(source, cancellationToken);
            source.ConnectionId = $"pending-{Guid.NewGuid():N}";
            source.LastSyncRequestedAt = null;
        }
        source.SourceKind = Bounded(input.DisplayName, 80) ?? connector.DisplayName;
        source.Status = "connecting";
        source.AuthorizationAcceptedAt = DateTimeOffset.UtcNow;
        source.AuthorizationNoticeVersion = AuthorizationNoticeVersion;
        source.LastCheckedAt = DateTimeOffset.UtcNow;
        source.LastError = null;
        source.LastWarning = null;
        source.SafeMetadataJson = JsonSerializer.Serialize(new
        {
            connectorKey = connector.Key,
            sourceFamily = connector.SourceFamily,
            generationProjectId = project.Id,
            sourceId = source.Id,
            providerConfigKey,
            nangoEnvironment = options.Value.Environment,
            authorizationNoticeVersion = AuthorizationNoticeVersion,
            authorizationAcceptedAt = source.AuthorizationAcceptedAt
        }, JsonOptions);
        await dbContext.SaveChangesAsync(cancellationToken);

        using var request = Authorized(HttpMethod.Post, "/connect/sessions");
        var sessionPayload = new Dictionary<string, object>
        {
            ["tags"] = new Dictionary<string, string>
            {
                ["end_user_id"] = customerId.ToString(),
                ["generation_project_id"] = projectId.ToString(),
                ["source_id"] = source.Id.ToString(),
                ["source_family"] = connector.SourceFamily,
                ["provider_config_key"] = providerConfigKey,
                ["nango_environment"] = options.Value.Environment
            },
            ["allowed_integrations"] = new[] { providerConfigKey }
        };
        var connectionWebhookUrl = ConnectionWebhookUrl();
        if (connectionWebhookUrl is not null)
            sessionPayload["webhook_url_override"] = connectionWebhookUrl;
        request.Content = JsonContent.Create(sessionPayload, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!response.IsSuccessStatusCode)
        {
            source.Status = "connection_failed";
            source.LastError = "Document connection could not be started.";
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Document connection could not be started. Please retry or contact support.");
        }
        var data = document.RootElement.TryGetProperty("data", out var payload) ? payload : default;
        var token = ReadString(data, "token");
        var expiresAt = ReadString(data, "expires_at");
        var connectLink = ReadString(data, "connect_link");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expiresAt))
            throw new InvalidOperationException("Document connector returned an invalid response.");
        if (string.IsNullOrWhiteSpace(connectLink))
            connectLink = $"{options.Value.ConnectBaseUrl.TrimEnd('/')}/?session_token={Uri.EscapeDataString(token)}";
        return new NangoConnectSessionViewModel(token, connectLink, expiresAt, Map(source));
    }

    public async Task<ReconcileInputSourcesViewModel> ReconcileAsync(
        Guid customerId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        if (!await dbContext.GenerationProjects.AnyAsync(
                value => value.Id == projectId && value.CustomerId == customerId, cancellationToken))
            throw new KeyNotFoundException("Generation project was not found.");
        var sources = await dbContext.GenerationProjectInputSources
            .Where(value => value.GenerationProjectId == projectId)
            .OrderBy(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        if (sources.Count == 0) return new ReconcileInputSourcesViewModel([], 0, 0);

        var connections = await ListConnectionsAsync(cancellationToken);
        var reconciled = 0;
        var removed = 0;
        var completedSourceIds = new HashSet<Guid>();
        var schedulesToStart = new List<GenerationProjectInputSource>();
        foreach (var source in sources.ToArray())
        {
            source.LastCheckedAt = DateTimeOffset.UtcNow;
            var connection = connections.FirstOrDefault(value =>
                ReadString(value, "provider_config_key") == source.ProviderConfigKey &&
                (ReadString(value, "connection_id") == source.ConnectionId || TagsMatch(value, projectId, source.Id)));
            if (connection.ValueKind == JsonValueKind.Undefined)
            {
                if (source.Status is "draft" or "connecting" && source.UpdatedAt < DateTimeOffset.UtcNow.AddMinutes(-15))
                {
                    dbContext.GenerationProjectInputSources.Remove(source);
                    sources.Remove(source);
                    removed++;
                }
                else if (!source.ConnectionId.StartsWith("pending-", StringComparison.Ordinal))
                {
                    source.Status = "requires_reconnect";
                    source.LastError = "The connector authorization no longer exists.";
                }
                continue;
            }

            reconciled++;
            source.ConnectionId = ReadString(connection, "connection_id");
            if (connection.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                source.Status = "requires_reconnect";
                source.LastError = "The connector needs to be authorized again.";
                continue;
            }

            source.LastError = null;
            if (source.ProviderConfigKey == "notion" && source.ScopeMode == "unconfigured")
            {
                source.Status = "awaiting_scope";
                source.LastWarning = "Choose which Notion content ErpGenerator may use.";
                continue;
            }

            if (source.LastSyncRequestedAt is not null)
            {
                var sync = await GetSyncStatusAsync(source, cancellationToken);
                var outcome = SyncOutcome(sync, source.LastSyncRequestedAt.Value);
                if (outcome == SyncExecutionOutcome.Running)
                {
                    source.Status = "syncing";
                    continue;
                }
                if (outcome == SyncExecutionOutcome.Failed)
                {
                    source.Status = "sync_failed";
                    source.LastError = "The connector synchronization failed. Check the source and try again.";
                    continue;
                }
                if (outcome == SyncExecutionOutcome.Succeeded)
                {
                    source.Status = "syncing";
                    completedSourceIds.Add(source.Id);
                    var scheduleStatus = ReadString(sync, "status").ToUpperInvariant();
                    if (source.IncludeNewContent && scheduleStatus is "PAUSED" or "STOPPED")
                        schedulesToStart.Add(source);
                    continue;
                }
            }

            if (source.Status is "draft" or "connecting" or "connected" or "authenticated" or
                "error" or "connection_failed")
            {
                if (source.ProviderConfigKey == "notion")
                    await UpdateNangoMetadataAsync(
                        source,
                        SelectedIds(source),
                        ExcludedIds(source),
                        cancellationToken);
                await StartSyncScheduleAsync(source, cancellationToken);
                source.Status = "syncing";
                source.LastSyncRequestedAt = DateTimeOffset.UtcNow;
                source.LastError = null;
                source.LastWarning = null;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (completedSourceIds.Count > 0)
            await ImportConnectedEvidenceAsync(customerId, projectId, completedSourceIds, cancellationToken);
        foreach (var source in schedulesToStart)
            await StartSyncScheduleAsync(source, cancellationToken);
        return new ReconcileInputSourcesViewModel(sources.Select(Map).ToArray(), reconciled, removed);
    }

    public async Task<ConnectorResourceListViewModel> ListResourcesAsync(
        Guid customerId,
        Guid projectId,
        Guid sourceId,
        string? cursor,
        string? query,
        CancellationToken cancellationToken)
    {
        var source = await OwnedSourceAsync(customerId, projectId, sourceId, cancellationToken);
        if (source.ProviderConfigKey != "notion")
            throw new ArgumentException("Content selection is currently available for Notion sources.");
        EnsureConnected(source);
        var (resources, nextCursor) = await ListNotionResourcesAsync(source, cursor, query, cancellationToken);
        source.LastCheckedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ConnectorResourceListViewModel(Map(source), resources, nextCursor);
    }

    public async Task<GenerationInputSourceViewModel> UpdateSelectionAsync(
        Guid customerId,
        Guid projectId,
        Guid sourceId,
        UpdateInputSourceSelectionInputModel input,
        CancellationToken cancellationToken)
    {
        var source = await OwnedSourceAsync(customerId, projectId, sourceId, cancellationToken, includeRecords: true);
        if (source.ProviderConfigKey != "notion")
            throw new ArgumentException("Content selection is currently available for Notion sources.");
        EnsureConnected(source);
        var mode = input.Mode?.Trim().ToLowerInvariant();
        if (mode is not ("all_accessible" or "selected_roots"))
            throw new ArgumentException("Choose either all accessible content or specific areas.");
        var selected = mode == "selected_roots"
            ? NormalizeResourceIds(input.IncludedRootIds)
            : [];
        IReadOnlyList<string> excluded = [];
        if (mode == "selected_roots" && selected.Count == 0)
            throw new ArgumentException("Choose at least one Notion area.");

        source.ScopeMode = mode;
        source.SelectedExternalIdsJson = JsonSerializer.Serialize(selected, JsonOptions);
        source.ExcludedExternalIdsJson = JsonSerializer.Serialize(excluded, JsonOptions);
        source.IncludeDescendants = true;
        source.IncludeNewContent = true;
        source.LastError = null;
        source.LastWarning = null;
        source.LastCheckedAt = DateTimeOffset.UtcNow;
        source.SyncDiscoveredCount = null;
        source.SyncProcessedCount = 0;
        source.SyncFailedCount = 0;
        await UpdateNangoMetadataAsync(source, selected, excluded, cancellationToken);
        dbContext.NangoSyncedRecords.RemoveRange(source.SyncedRecords);
        source.LastRecordCount = 0;
        source.LastTextRecordCount = 0;
        var firstSynchronization = source.LastSyncRequestedAt is null;
        if (firstSynchronization)
            await StartSyncScheduleAsync(source, cancellationToken);
        else
            await TriggerSyncAsync(source, reset: true, cancellationToken);
        source.Status = "syncing";
        source.LastSyncRequestedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(source);
    }

    public async Task<GenerationInputSourceViewModel> RefreshAsync(
        Guid customerId,
        Guid projectId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var source = await OwnedSourceAsync(customerId, projectId, sourceId, cancellationToken);
        EnsureConnected(source);
        if (source.ProviderConfigKey == "notion" && source.ScopeMode == "unconfigured")
            throw new InvalidOperationException("Choose which Notion content may be synchronized first.");

        await TriggerSyncAsync(source, reset: false, cancellationToken);
        source.Status = "syncing";
        source.LastSyncRequestedAt = DateTimeOffset.UtcNow;
        source.LastCheckedAt = DateTimeOffset.UtcNow;
        source.LastError = null;
        source.LastWarning = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(source);
    }

    public async Task DisconnectAsync(
        Guid customerId,
        Guid projectId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var source = await OwnedSourceAsync(customerId, projectId, sourceId, cancellationToken, includeRecords: true);
        await DeleteNangoConnectionAsync(source, cancellationToken);
        dbContext.GenerationProjectInputSources.Remove(source);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DisconnectProjectSourcesAsync(
        Guid customerId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var sources = await dbContext.GenerationProjectInputSources
            .Where(value => value.GenerationProjectId == projectId && value.GenerationProject.CustomerId == customerId)
            .ToListAsync(cancellationToken);
        foreach (var source in sources)
            await DeleteNangoConnectionAsync(source, cancellationToken);
        dbContext.GenerationProjectInputSources.RemoveRange(sources);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<int> ImportConnectedEvidenceAsync(
        Guid customerId,
        Guid projectId,
        CancellationToken cancellationToken) =>
        ImportConnectedEvidenceAsync(customerId, projectId, sourceIds: null, cancellationToken);

    private async Task<int> ImportConnectedEvidenceAsync(
        Guid customerId,
        Guid projectId,
        IReadOnlySet<Guid>? sourceIds,
        CancellationToken cancellationToken)
    {
        var query = dbContext.GenerationProjectInputSources
            .Include(value => value.SyncedRecords)
            .Where(value => value.GenerationProjectId == projectId &&
                            value.GenerationProject.CustomerId == customerId &&
                            value.Status != "connecting" &&
                            value.Status != "awaiting_scope" &&
                            value.Status != "requires_reconnect" &&
                            value.Status != "connection_failed" &&
                            value.Status != "sync_failed" &&
                            value.Status != "error" &&
                            value.Status != "expired");
        if (sourceIds is not null)
            query = query.Where(value => sourceIds.Contains(value.Id));
        var sources = await query.OrderBy(value => value.CreatedAt).ToListAsync(cancellationToken);
        if (sources.Count == 0) return 0;
        EnsureConfigured();

        var imported = 0;
        foreach (var source in sources)
        {
            var connector = ConnectorFor(source);
            if (connector is null) continue;
            var model = ModelFor(connector);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var currentById = source.SyncedRecords
                .Where(value => value.RecordModel == model)
                .ToDictionary(value => value.ExternalId, StringComparer.Ordinal);
            string? cursor = null;
            var observed = 0;
            do
            {
                using var request = Authorized(HttpMethod.Get,
                    $"/records?model={Uri.EscapeDataString(model)}&limit=100" +
                    (string.IsNullOrWhiteSpace(cursor) ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"));
                request.Headers.Add("Connection-Id", source.ConnectionId);
                request.Headers.Add("Provider-Config-Key", source.ProviderConfigKey);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Connected source evidence could not be refreshed.");
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Connected source returned an invalid records response.");

                foreach (var payload in records.EnumerateArray())
                {
                    observed++;
                    var externalId = First(payload, "id", "key", "uid");
                    if (IsDeleted(payload))
                    {
                        currentById.TryGetValue(externalId, out var deleted);
                        if (deleted is not null) dbContext.NangoSyncedRecords.Remove(deleted);
                        continue;
                    }

                    var mapped = MapRecord(payload, model, source.ProviderConfigKey);
                    if (mapped is null || !seen.Add(mapped.ExternalId)) continue;
                    currentById.TryGetValue(mapped.ExternalId, out var current);
                    if (current is null)
                    {
                        current = new NangoSyncedRecord
                        {
                            GenerationProjectInputSource = source,
                            ExternalId = mapped.ExternalId,
                            RecordModel = model
                        };
                        source.SyncedRecords.Add(current);
                        dbContext.NangoSyncedRecords.Add(current);
                        currentById[mapped.ExternalId] = current;
                    }
                    current.Provider = mapped.Provider;
                    current.Title = mapped.Title;
                    current.ContentState = mapped.ContentState;
                    current.Content = mapped.Content;
                    current.ContentSha256 = Sha256(mapped.Content);
                    current.SourceUrl = mapped.SourceUrl;
                    current.SafeMetadataJson = mapped.SafeMetadataJson;
                    current.UpstreamModifiedAt = mapped.ModifiedAt;
                    current.LastSeenAt = DateTimeOffset.UtcNow;
                    imported++;
                }
                cursor = ReadString(document.RootElement, "next_cursor");
            }
            while (!string.IsNullOrWhiteSpace(cursor));

            var stale = source.SyncedRecords
                .Where(value => value.RecordModel == model && !seen.Contains(value.ExternalId))
                .ToArray();
            dbContext.NangoSyncedRecords.RemoveRange(stale);

            var active = source.SyncedRecords
                .Where(value => dbContext.Entry(value).State != EntityState.Deleted)
                .ToArray();
            var textRecords = active.Count(value =>
                value.ContentState == "content_extracted" && !string.IsNullOrWhiteSpace(value.Content));
            source.LastRecordCount = active.Length;
            source.LastTextRecordCount = textRecords;
            source.SyncDiscoveredCount = observed;
            source.SyncProcessedCount = active.Length;
            source.SyncFailedCount = active.Count(value => value.ContentState == "extraction_failed");
            source.LastSyncedAt = DateTimeOffset.UtcNow;
            source.LastCheckedAt = DateTimeOffset.UtcNow;
            source.RetainContentUntil = DateTimeOffset.UtcNow.AddDays(
                Math.Clamp(options.Value.ConnectorContentRetentionDays, 1, 365));
            source.LastError = null;
            source.LastWarning = active.Length > textRecords
                ? $"{active.Length - textRecords} connector records did not contain readable evidence."
                : null;
            source.Status = active.Length == 0
                ? "no_records"
                : textRecords == 0
                    ? "records_without_text"
                    : "ready";
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return imported;
    }

    public async Task HandleWebhookAsync(string body, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var connectionId = DeepString(root, "connectionId", "connection_id");
        var providerConfigKey = DeepString(root, "providerConfigKey", "provider_config_key");
        var sourceIdText = DeepTag(root, "source_id");
        GenerationProjectInputSource? source = null;
        if (Guid.TryParse(sourceIdText, out var sourceId))
            source = await dbContext.GenerationProjectInputSources.SingleOrDefaultAsync(
                value => value.Id == sourceId, cancellationToken);
        if (source is null && !string.IsNullOrWhiteSpace(connectionId))
            source = await dbContext.GenerationProjectInputSources.SingleOrDefaultAsync(
                value => value.ConnectionId == connectionId &&
                         (string.IsNullOrWhiteSpace(providerConfigKey) || value.ProviderConfigKey == providerConfigKey),
                cancellationToken);
        if (source is null) return;

        if (!string.IsNullOrWhiteSpace(connectionId)) source.ConnectionId = connectionId;
        source.LastCheckedAt = DateTimeOffset.UtcNow;
        var eventName = First(root, "type", "event").ToLowerInvariant();
        var success = !eventName.Contains("fail") && !eventName.Contains("error") &&
                      !string.Equals(DeepString(root, "success"), "false", StringComparison.OrdinalIgnoreCase);
        if (!success)
        {
            source.Status = "sync_failed";
            source.LastError = "The connector reported a synchronization failure.";
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var owner = await dbContext.GenerationProjects.AsNoTracking()
            .Where(value => value.Id == source.GenerationProjectId)
            .Select(value => new { value.CustomerId, value.Id })
            .SingleAsync(cancellationToken);
        if (eventName.Contains("sync"))
        {
            source.Status = "syncing";
            await dbContext.SaveChangesAsync(cancellationToken);
            await ImportConnectedEvidenceAsync(owner.CustomerId, owner.Id, cancellationToken);
        }
        else if (eventName.Contains("connection") || source.Status == "connecting")
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await ReconcileAsync(owner.CustomerId, owner.Id, cancellationToken);
        }
    }

    public async Task<int> PurgeExpiredContentAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var sources = await dbContext.GenerationProjectInputSources
            .Include(value => value.SyncedRecords)
            .Where(value => value.RetainContentUntil != null && value.RetainContentUntil <= now)
            .ToListAsync(cancellationToken);
        var removed = sources.Sum(value => value.SyncedRecords.Count);
        foreach (var source in sources)
        {
            dbContext.NangoSyncedRecords.RemoveRange(source.SyncedRecords);
            source.LastRecordCount = 0;
            source.LastTextRecordCount = 0;
            source.RetainContentUntil = null;
            source.Status = "expired";
            source.LastWarning = "Previously synchronized content expired. Refresh the source to use it again.";
        }
        if (sources.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
        return removed;
    }

    private async Task<GenerationProjectInputSource> OwnedSourceAsync(
        Guid customerId,
        Guid projectId,
        Guid sourceId,
        CancellationToken cancellationToken,
        bool includeRecords = false)
    {
        IQueryable<GenerationProjectInputSource> query = dbContext.GenerationProjectInputSources;
        if (includeRecords) query = query.Include(value => value.SyncedRecords);
        return await query.SingleOrDefaultAsync(value =>
                   value.Id == sourceId &&
                   value.GenerationProjectId == projectId &&
                   value.GenerationProject.CustomerId == customerId,
                   cancellationToken)
               ?? throw new KeyNotFoundException("Connected source was not found.");
    }

    private async Task<JsonElement[]> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, "/connection?limit=100");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Unable to check document connections.");
        return document.RootElement.TryGetProperty("connections", out var array) &&
               array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(value => value.Clone()).ToArray()
            : [];
    }

    private async Task<(IReadOnlyList<ConnectorResourceViewModel> Resources, string? NextCursor)> ListNotionResourcesAsync(
        GenerationProjectInputSource source,
        string? cursor,
        string? query,
        CancellationToken cancellationToken)
    {
        _ = cursor;
        var selected = SelectedIds(source).ToHashSet(StringComparer.Ordinal);
        var discovered = new Dictionary<string, ConnectorResourceViewModel>(StringComparer.Ordinal);
        string? searchCursor = null;
        do
        {
            using var request = Authorized(HttpMethod.Post, "/proxy/v1/search");
            AddNotionHeaders(request, source);
            var body = new Dictionary<string, object> { ["page_size"] = 100 };
            if (!string.IsNullOrWhiteSpace(searchCursor)) body["start_cursor"] = searchCursor;
            request.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Notion content could not be listed.");
            var root = document.RootElement;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var id = ReadString(item, "id");
                    var kind = NotionKind(item);
                    if (string.IsNullOrWhiteSpace(id) || kind == "database_row") continue;
                    discovered[id] = new ConnectorResourceViewModel(
                        id,
                        NotionTitle(item),
                        kind,
                        BoundedText(ReadString(item, "url"), 2000),
                        ParseDate(ReadString(item, "last_edited_time")),
                        selected.Contains(id),
                        NotionParentId(item));
                }
            }
            searchCursor = ReadBoolean(root, "has_more") ? ReadString(root, "next_cursor") : null;
        } while (!string.IsNullOrWhiteSpace(searchCursor));

        var childOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var parentsWithMultipleChildren = discovered.Values
            .Where(value => value.ParentId is not null && discovered.ContainsKey(value.ParentId))
            .GroupBy(value => value.ParentId!, StringComparer.Ordinal)
            .Where(value => value.Skip(1).Any())
            .Select(value => value.Key)
            .ToArray();
        foreach (var parentId in parentsWithMultipleChildren)
            await ReadNotionChildOrderAsync(source, parentId, childOrder, cancellationToken);

        var resources = discovered.Values
            .Select(value => value with
            {
                SortIndex = childOrder.TryGetValue(value.Id, out var index) ? index : null
            })
            .ToArray();
        var ordered = OrderNotionResources(resources);
        if (!string.IsNullOrWhiteSpace(query))
            ordered = FilterNotionResources(ordered, query.Trim());
        return (ordered, null);
    }

    private async Task ReadNotionChildOrderAsync(
        GenerationProjectInputSource source,
        string parentId,
        IDictionary<string, int> childOrder,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        var index = 0;
        do
        {
            var path = $"/proxy/v1/blocks/{Uri.EscapeDataString(parentId)}/children?page_size=100";
            if (!string.IsNullOrWhiteSpace(cursor))
                path += $"&start_cursor={Uri.EscapeDataString(cursor)}";
            using var request = Authorized(HttpMethod.Get, path);
            AddNotionHeaders(request, source);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var id = ReadString(item, "id");
                    if (!string.IsNullOrWhiteSpace(id) && !childOrder.ContainsKey(id))
                        childOrder[id] = index;
                    index++;
                }
            }
            cursor = ReadBoolean(root, "has_more") ? ReadString(root, "next_cursor") : null;
        } while (!string.IsNullOrWhiteSpace(cursor));
    }

    private static IReadOnlyList<ConnectorResourceViewModel> OrderNotionResources(
        IReadOnlyCollection<ConnectorResourceViewModel> resources)
    {
        var ids = resources.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var children = resources
            .Where(value => value.ParentId is not null && ids.Contains(value.ParentId))
            .GroupBy(value => value.ParentId!, StringComparer.Ordinal)
            .ToDictionary(
                value => value.Key,
                value => SortNotionSiblings(value).ToArray(),
                StringComparer.Ordinal);
        var ordered = new List<ConnectorResourceViewModel>(resources.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(ConnectorResourceViewModel resource)
        {
            if (!visited.Add(resource.Id)) return;
            ordered.Add(resource);
            if (!children.TryGetValue(resource.Id, out var nested)) return;
            foreach (var child in nested) Visit(child);
        }

        foreach (var root in SortNotionSiblings(resources.Where(value =>
                     value.ParentId is null || !ids.Contains(value.ParentId))))
            Visit(root);
        foreach (var remaining in SortNotionSiblings(resources.Where(value => !visited.Contains(value.Id))))
            Visit(remaining);
        return ordered;
    }

    private static IOrderedEnumerable<ConnectorResourceViewModel> SortNotionSiblings(
        IEnumerable<ConnectorResourceViewModel> resources) =>
        resources
            .OrderBy(value => value.SortIndex ?? int.MaxValue)
            .ThenBy(value => value.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Id, StringComparer.Ordinal);

    private static IReadOnlyList<ConnectorResourceViewModel> FilterNotionResources(
        IReadOnlyList<ConnectorResourceViewModel> resources,
        string query)
    {
        var byId = resources.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in resources.Where(value =>
                     value.Title.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            ConnectorResourceViewModel? current = match;
            while (current is not null && included.Add(current.Id))
                current = current.ParentId is not null && byId.TryGetValue(current.ParentId, out var parent)
                    ? parent
                    : null;
        }
        return resources.Where(value => included.Contains(value.Id)).ToArray();
    }

    private static void AddNotionHeaders(
        HttpRequestMessage request,
        GenerationProjectInputSource source)
    {
        request.Headers.Add("Provider-Config-Key", source.ProviderConfigKey);
        request.Headers.Add("Connection-Id", source.ConnectionId);
        request.Headers.Add("Notion-Version", "2022-06-28");
    }

    private async Task UpdateNangoMetadataAsync(
        GenerationProjectInputSource source,
        IReadOnlyList<string> selected,
        IReadOnlyList<string> excluded,
        CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Patch, "/connections/metadata");
        request.Content = JsonContent.Create(new
        {
            connection_id = source.ConnectionId,
            provider_config_key = source.ProviderConfigKey,
            metadata = new
            {
                notionScope = new
                {
                    mode = source.ScopeMode,
                    includedRootIds = selected,
                    excludedIds = excluded,
                    includeDescendants = source.IncludeDescendants,
                    includeNewContent = source.IncludeNewContent
                }
            }
        }, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The connector content selection could not be saved.");
    }

    private async Task TriggerSyncAsync(
        GenerationProjectInputSource source,
        bool reset,
        CancellationToken cancellationToken)
    {
        var connector = ConnectorFor(source)
            ?? throw new InvalidOperationException("The connector configuration is unsupported.");
        using var request = Authorized(HttpMethod.Post, "/sync/trigger");
        var body = new Dictionary<string, object>
        {
            ["provider_config_key"] = source.ProviderConfigKey,
            ["syncs"] = new[] { SyncNameFor(connector) },
            ["connection_id"] = source.ConnectionId
        };
        if (reset) body["opts"] = new { reset = true, emptyCache = true };
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The connector synchronization could not be started.");
    }

    private async Task StartSyncScheduleAsync(
        GenerationProjectInputSource source,
        CancellationToken cancellationToken)
    {
        var connector = ConnectorFor(source)
            ?? throw new InvalidOperationException("The connector configuration is unsupported.");
        using var request = Authorized(HttpMethod.Post, "/sync/start");
        request.Content = JsonContent.Create(new
        {
            provider_config_key = source.ProviderConfigKey,
            syncs = new[] { SyncNameFor(connector) },
            connection_id = source.ConnectionId
        }, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The connector synchronization schedule could not be started.");
    }

    private async Task<JsonElement> GetSyncStatusAsync(
        GenerationProjectInputSource source,
        CancellationToken cancellationToken)
    {
        var connector = ConnectorFor(source);
        if (connector is null) return default;
        using var request = Authorized(HttpMethod.Get,
            $"/sync/status?provider_config_key={Uri.EscapeDataString(source.ProviderConfigKey)}" +
            $"&syncs={Uri.EscapeDataString(SyncNameFor(connector))}" +
            $"&connection_id={Uri.EscapeDataString(source.ConnectionId)}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return default;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("syncs", out var syncs) &&
               syncs.ValueKind == JsonValueKind.Array && syncs.GetArrayLength() > 0
            ? syncs[0].Clone()
            : default;
    }

    private async Task DeleteNangoConnectionAsync(
        GenerationProjectInputSource source,
        CancellationToken cancellationToken)
    {
        if (source.ConnectionId.StartsWith("pending-", StringComparison.Ordinal)) return;
        using var request = Authorized(HttpMethod.Delete,
            $"/connections/{Uri.EscapeDataString(source.ConnectionId)}" +
            $"?provider_config_key={Uri.EscapeDataString(source.ProviderConfigKey)}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return;
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error) &&
                    ReadString(error, "code") == "unknown_connection") return;
            }
            catch (JsonException) { }
        }
        throw new InvalidOperationException("The external connector could not be disconnected.");
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(options.Value.BaseUrl.TrimEnd('/')), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);
        return request;
    }

    private Connector? ConnectorFor(GenerationProjectInputSource source) =>
        Connectors.SingleOrDefault(value =>
            value.ProviderConfigKeys.Contains(source.ProviderConfigKey, StringComparer.OrdinalIgnoreCase));

    private string? ResolveProviderConfigKey(Connector connector)
    {
        var allowed = options.Value.AllowedFileIntegrations.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return connector.ProviderConfigKeys.FirstOrDefault(key =>
            allowed.Contains(key, StringComparer.OrdinalIgnoreCase));
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(options.Value.SecretKey) || AvailableConnectorKeys.Count == 0)
            throw new InvalidOperationException("Document connectors are not configured for this environment.");
        _ = ConnectionWebhookUrl();
    }

    private string? ConnectionWebhookUrl()
    {
        var value = options.Value.ConnectionWebhookUrl.Trim();
        if (value.Length == 0) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "The connector webhook URL must be an absolute HTTPS URL without a query or fragment.");
        }
        return uri.ToString();
    }

    private static void EnsureConnected(GenerationProjectInputSource source)
    {
        if (source.ConnectionId.StartsWith("pending-", StringComparison.Ordinal) ||
            source.Status is "connecting" or "draft")
            throw new InvalidOperationException("Finish authorizing this connector first.");
        if (source.Status is "requires_reconnect" or "connection_failed")
            throw new InvalidOperationException("Authorize this connector again before continuing.");
    }

    private static SyncExecutionOutcome SyncOutcome(
        JsonElement sync,
        DateTimeOffset requestedAt)
    {
        if (sync.ValueKind != JsonValueKind.Object)
            return SyncExecutionOutcome.Running;

        var scheduleStatus = ReadString(sync, "status").ToUpperInvariant();
        var executionStatus = ReadString(sync, "latestExecutionStatus");
        if (string.IsNullOrWhiteSpace(executionStatus))
            executionStatus = ReadString(sync, "latest_execution_status");
        executionStatus = executionStatus.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(executionStatus) &&
            scheduleStatus is "RUNNING" or "SUCCESS" or "ERROR")
            executionStatus = scheduleStatus;

        if (executionStatus == "RUNNING" || scheduleStatus == "RUNNING")
            return SyncExecutionOutcome.Running;

        var finishedAt = ParseDate(First(sync, "finishedAt", "finished_at"));
        var belongsToRequestedRun = finishedAt is not null && finishedAt >= requestedAt;
        if (executionStatus == "SUCCESS" && belongsToRequestedRun)
            return SyncExecutionOutcome.Succeeded;
        if (executionStatus == "ERROR" && (finishedAt is null || belongsToRequestedRun))
            return SyncExecutionOutcome.Failed;
        if (scheduleStatus == "ERROR" && (finishedAt is null || belongsToRequestedRun))
            return SyncExecutionOutcome.Failed;
        if (scheduleStatus == "STOPPED" && executionStatus != "SUCCESS")
            return SyncExecutionOutcome.Failed;

        return SyncExecutionOutcome.Running;
    }

    private static IReadOnlyList<string> NormalizeResourceIds(IReadOnlyList<string>? values)
    {
        var result = new List<string>();
        foreach (var value in values ?? [])
        {
            var id = value.Trim();
            if (id.Length is < 1 or > 100 || id.Any(char.IsControl))
                throw new ArgumentException("A selected connector resource is invalid.");
            if (!result.Contains(id, StringComparer.Ordinal)) result.Add(id);
        }
        return result;
    }

    private static IReadOnlyList<string> SelectedIds(GenerationProjectInputSource source)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(source.SelectedExternalIdsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ExcludedIds(GenerationProjectInputSource source)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(source.ExcludedExternalIdsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TagsMatch(JsonElement connection, Guid projectId, Guid sourceId)
    {
        if (!connection.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return false;
        var project = ReadString(tags, "generation_project_id");
        var source = ReadString(tags, "source_id");
        return project == projectId.ToString() &&
               (source == sourceId.ToString() || string.IsNullOrWhiteSpace(source));
    }

    private static string ModelFor(Connector connector) =>
        connector.SourceFamily == "messages" ? "UnifiedMessage" : "UnifiedDocument";

    private static string SyncNameFor(Connector connector) =>
        connector.SourceFamily == "messages" ? "messages" : "documents";

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static MappedRecord? MapRecord(JsonElement payload, string model, string fallbackProvider)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        var externalId = First(payload, "id", "key", "uid");
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 512) return null;
        var provider = BoundedText(First(payload, "provider"), 120) ?? fallbackProvider;
        string title;
        string content;
        string contentState;
        DateTimeOffset? modifiedAt;
        if (model == "UnifiedMessage")
        {
            title = BoundedText(First(payload, "channelOrThread", "author"), 240) ?? "Imported message";
            var author = BoundedText(First(payload, "author"), 240);
            var message = First(payload, "text");
            if (string.IsNullOrWhiteSpace(message)) return null;
            content = string.Join("\n\n", new[]
            {
                $"Channel/thread: {title}",
                author is null ? null : $"Author: {author}",
                message
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            contentState = "content_extracted";
            modifiedAt = ParseDate(First(payload, "timestamp", "modifiedAt", "updatedAt"));
        }
        else
        {
            title = BoundedText(First(payload, "title", "name"), 240) ?? "Imported document";
            content = First(payload, "text");
            contentState = BoundedText(First(payload, "contentState"), 32) ?? "metadata_only";
            modifiedAt = ParseDate(First(payload, "modifiedAt", "updatedAt"));
        }
        var sourceUrl = BoundedText(First(payload, "sourceUrl", "url"), 2000);
        return new MappedRecord(
            externalId,
            provider,
            title,
            contentState,
            content,
            sourceUrl,
            SafeMetadata(payload),
            modifiedAt);
    }

    private static bool IsDeleted(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        foreach (var metadataName in new[] { "_nango_metadata", "_metadata" })
        {
            if (!payload.TryGetProperty(metadataName, out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object) continue;
            if (metadata.TryGetProperty("deleted", out var deleted) &&
                deleted.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                deleted.GetBoolean())
                return true;
            if (metadata.TryGetProperty("deleted_at", out var deletedAt) &&
                deletedAt.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(deletedAt.GetString()))
                return true;
            if (string.Equals(
                    ReadString(metadata, "last_action"),
                    "DELETED",
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NotionTitle(JsonElement page)
    {
        if (page.TryGetProperty("title", out var objectTitle) &&
            objectTitle.ValueKind == JsonValueKind.Array)
        {
            var value = string.Concat(objectTitle.EnumerateArray().Select(item => ReadString(item, "plain_text")));
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        if (page.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object ||
                    ReadString(property.Value, "type") != "title" ||
                    !property.Value.TryGetProperty("title", out var title) ||
                    title.ValueKind != JsonValueKind.Array) continue;
                var value = string.Concat(title.EnumerateArray().Select(item => ReadString(item, "plain_text")));
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return ReadString(page, "id");
    }

    private static string NotionKind(JsonElement page)
    {
        if (ReadString(page, "object") is "database" or "data_source") return "database";
        if (!page.TryGetProperty("parent", out var parent) || parent.ValueKind != JsonValueKind.Object)
            return "page";
        return ReadString(parent, "type") switch
        {
            "database_id" or "data_source_id" => "database_row",
            "page_id" => "subpage",
            _ => "page"
        };
    }

    private static string? NotionParentId(JsonElement item)
    {
        if (!item.TryGetProperty("parent", out var parent) || parent.ValueKind != JsonValueKind.Object)
            return null;
        var parentId = First(parent, "page_id", "database_id", "data_source_id");
        return string.IsNullOrWhiteSpace(parentId) ? null : parentId;
    }

    private static string First(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadString(element, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private static string DeepString(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()?.Trim() ?? string.Empty
                        : property.Value.ToString();
                var nested = DeepString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = DeepString(item, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return string.Empty;
    }

    private static string DeepTag(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                var value = ReadString(tags, name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            foreach (var property in element.EnumerateObject())
            {
                var nested = DeepTag(property.Value, name);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = DeepTag(item, name);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return string.Empty;
    }

    private static string SafeMetadata(JsonElement payload)
    {
        var safe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in payload.EnumerateObject())
        {
            var normalized = property.Name.Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            if (normalized.Contains("token") || normalized.Contains("secret") || normalized.Contains("password") ||
                normalized.Contains("authorization") || property.Name == "text" ||
                property.Name == "_nango_metadata") continue;
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or
                JsonValueKind.True or JsonValueKind.False)
            {
                var value = property.Value.ToString();
                if (value.Length <= 500) safe[property.Name] = value;
            }
            if (safe.Count == 30) break;
        }
        return JsonSerializer.Serialize(safe, JsonOptions);
    }

    private static string? BoundedText(string? value, int max)
    {
        var result = value?.Trim();
        if (string.IsNullOrWhiteSpace(result)) return null;
        return result.Length <= max ? result : result[..max];
    }

    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? Bounded(string? value, int max)
    {
        var result = value?.Trim();
        if (string.IsNullOrWhiteSpace(result)) return null;
        if (result.Length > max || result.Any(char.IsControl))
            throw new ArgumentException("Connector name is invalid.");
        return result;
    }

    public static GenerationInputSourceViewModel Map(GenerationProjectInputSource source)
    {
        var selected = SelectedIds(source).Count;
        var excluded = ExcludedIds(source).Count;
        return new GenerationInputSourceViewModel(
            source.Id,
            source.ProviderConfigKey,
            source.SourceKind,
            source.Status,
            source.LastSyncedAt,
            source.LastRecordCount,
            source.LastError,
            source.LastCheckedAt,
            source.LastTextRecordCount,
            source.LastWarning,
            source.LastSyncRequestedAt,
            selected,
            source.ProviderConfigKey == "notion" && source.ScopeMode == "unconfigured",
            source.RetainContentUntil,
            source.ScopeMode,
            excluded,
            source.IncludeDescendants,
            source.IncludeNewContent,
            source.SyncDiscoveredCount,
            source.SyncProcessedCount,
            source.SyncFailedCount,
            SelectedIds(source),
            ExcludedIds(source),
            ConnectorEvidenceReadiness.BlockingReason([source]));
    }

    private enum SyncExecutionOutcome
    {
        Running,
        Succeeded,
        Failed
    }
}
