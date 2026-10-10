using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Tests;

public sealed class NangoInputSourceServiceTests
{
    [Fact]
    public async Task Only_Notion_can_be_discovered_or_connected()
    {
        await using var db = CreateContext();
        var (customer, project) = await SeedProjectAsync(db);
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var service = CreateService(db, handler);
        Assert.All(service.AvailableConnectorKeys, key => Assert.Equal("notion", key));
        foreach (var key in new[] { "google-drive", "sharepoint", "microsoft-teams", "slack" })
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateConnectSessionAsync(customer.Id, project.Id, new(key, null, true), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task Connect_requires_the_versioned_authorization_acknowledgement_before_calling_nango()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"));
        var service = CreateService(dbContext, handler);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateConnectSessionAsync(
                customer.Id,
                project.Id,
                new CreateNangoConnectSessionInputModel("notion"),
                CancellationToken.None));

        Assert.Contains("data notice", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
        Assert.Empty(dbContext.GenerationProjectInputSources);
    }

    [Fact]
    public async Task Connect_session_routes_webhooks_to_the_configured_public_portal()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            {
              "data": {
                "token": "session-token",
                "expires_at": "2026-07-28T16:00:00Z",
                "connect_link": "https://connect.nango.test/session"
              }
            }
            """));
        var service = CreateService(
            dbContext,
            handler,
            connectionWebhookUrl: "https://portal.erpgenerator.test/api/integrations/nango/webhook");

        await service.CreateConnectSessionAsync(
            customer.Id,
            project.Id,
            new CreateNangoConnectSessionInputModel("notion", "Notion", true),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/connect/sessions", request.Uri.AbsolutePath);
        Assert.Contains(
            "\"webhook_url_override\":\"https://portal.erpgenerator.test/api/integrations/nango/webhook\"",
            request.Body,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Reconnect_replaces_the_failed_nango_connection_and_preserves_the_saved_scope(
        HttpStatusCode deletionStatus)
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(
            dbContext,
            project,
            status: "requires_reconnect",
            selectedExternalIdsJson: """["page-1"]""");
        var previousConnectionId = source.ConnectionId;
        source.LastSyncRequestedAt = DateTimeOffset.Parse("2026-07-26T11:00:00Z");
        await dbContext.SaveChangesAsync();
        var handler = new RecordingHandler(request => request.Uri.AbsolutePath switch
        {
            var path when path == $"/connections/{previousConnectionId}" =>
                Json(deletionStatus, """{"error":{"code":"unknown_connection"}}"""),
            "/connect/sessions" => Json(HttpStatusCode.OK, """
                {
                  "data": {
                    "token": "session-token",
                    "expires_at": "2026-07-26T15:00:00Z",
                    "connect_link": "https://connect.nango.test/session"
                  }
                }
                """),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        var service = CreateService(dbContext, handler);

        var result = await service.CreateConnectSessionAsync(
            customer.Id,
            project.Id,
            new CreateNangoConnectSessionInputModel("notion", "Notion", true),
            CancellationToken.None);

        Assert.Equal(
            [HttpMethod.Delete, HttpMethod.Post],
            handler.Requests.Select(value => value.Method).ToArray());
        Assert.Contains(
            $"provider_config_key={source.ProviderConfigKey}",
            handler.Requests[0].Uri.Query);
        Assert.Equal("connecting", source.Status);
        Assert.StartsWith("pending-", source.ConnectionId, StringComparison.Ordinal);
        Assert.NotEqual(previousConnectionId, source.ConnectionId);
        Assert.Null(source.LastSyncRequestedAt);
        Assert.Equal("selected_roots", source.ScopeMode);
        Assert.Equal("""["page-1"]""", source.SelectedExternalIdsJson);
        Assert.Equal("connector_setup_pending", result.InputSource.GenerationBlockReason);
    }

    [Fact]
    public async Task Reconnect_keeps_the_connection_when_nango_rejects_cleanup_for_another_reason()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(dbContext, project, status: "requires_reconnect");
        var previousConnectionId = source.ConnectionId;
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.BadRequest, """{"error":{"code":"invalid_request"}}"""));
        var service = CreateService(dbContext, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateConnectSessionAsync(
            customer.Id, project.Id, new("notion", "Notion", true), CancellationToken.None));

        Assert.Equal(HttpMethod.Delete, Assert.Single(handler.Requests).Method);
        Assert.Equal(previousConnectionId, source.ConnectionId);
        Assert.Equal("requires_reconnect", source.Status);
    }

    [Fact]
    public async Task Reconcile_leaves_a_new_notion_connection_waiting_for_selection_without_starting_a_sync()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(dbContext, project, status: "connected");
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("/connection", request.Uri.AbsolutePath);
            return Json(HttpStatusCode.OK, $$"""
                {
                  "connections": [{
                    "provider_config_key": "notion",
                    "connection_id": "{{source.ConnectionId}}",
                    "tags": {
                      "generation_project_id": "{{project.Id}}",
                      "source_id": "{{source.Id}}"
                    }
                  }]
                }
                """);
        });
        var service = CreateService(dbContext, handler);

        var result = await service.ReconcileAsync(customer.Id, project.Id, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal("awaiting_scope", Assert.Single(result.Sources).Status);
        Assert.True(result.Sources[0].RequiresSelection);
        Assert.Equal("awaiting_scope", source.Status);
        Assert.Contains("Choose", source.LastWarning);
    }

    [Fact]
    public async Task Reconcile_surfaces_a_failed_sync_without_importing_stale_cached_records()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(
            dbContext,
            project,
            status: "syncing",
            selectedExternalIdsJson: """["page-1"]""");
        source.LastSyncRequestedAt = DateTimeOffset.Parse("2026-07-26T11:00:00Z");
        await dbContext.SaveChangesAsync();
        var handler = new RecordingHandler(request => request.Uri.AbsolutePath switch
        {
            "/connection" => Json(HttpStatusCode.OK, $$"""
                {
                  "connections": [{
                    "provider_config_key": "notion",
                    "connection_id": "{{source.ConnectionId}}"
                  }]
                }
                """),
            "/sync/status" => Json(HttpStatusCode.OK, """
                {
                  "syncs": [{
                    "status": "ERROR",
                    "latestExecutionStatus": "ERROR",
                    "finishedAt": "2026-07-26T12:00:00Z"
                  }]
                }
                """),
            "/records" => throw new InvalidOperationException("Stale records must not be imported."),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        var service = CreateService(dbContext, handler);

        var result = await service.ReconcileAsync(
            customer.Id, project.Id, CancellationToken.None);

        Assert.Equal("sync_failed", Assert.Single(result.Sources).Status);
        Assert.Contains("failed", source.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, value => value.Uri.AbsolutePath == "/records");
    }

    [Fact]
    public async Task Reconcile_requires_reconnection_only_when_nango_reports_an_authorization_error()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(dbContext, project, status: "connecting");
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("/connection", request.Uri.AbsolutePath);
            return Json(HttpStatusCode.OK, $$"""
                {
                  "connections": [{
                    "provider_config_key": "notion",
                    "connection_id": "{{source.ConnectionId}}",
                    "errors": [{ "type": "auth", "message": "Token expired" }]
                  }]
                }
                """);
        });
        var service = CreateService(dbContext, handler);

        var result = await service.ReconcileAsync(customer.Id, project.Id, CancellationToken.None);

        Assert.Equal("requires_reconnect", Assert.Single(result.Sources).Status);
        Assert.Contains("authorized again", source.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Notion_resource_catalog_loads_every_page_and_uses_notion_child_order()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(
            dbContext,
            project,
            status: "awaiting_scope",
            selectedExternalIdsJson: """["page-01"]""");
        var searchPage = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.Uri.AbsolutePath == "/proxy/v1/blocks/root/children")
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "results": [
                        { "id": "page-00", "type": "child_page" },
                        { "id": "page-01", "type": "child_page" },
                        { "id": "page-07", "type": "child_page" }
                      ],
                      "has_more": false
                    }
                    """);
            }

            Assert.Equal("/proxy/v1/search", request.Uri.AbsolutePath);
            searchPage++;
            return searchPage == 1
                ? Json(HttpStatusCode.OK, """
                    {
                      "results": [
                        {
                          "id": "page-07",
                          "parent": { "type": "page_id", "page_id": "root" },
                          "properties": {
                            "Name": {
                              "type": "title",
                              "title": [{ "plain_text": "07_Machine_Ingestion_Guide" }]
                            }
                          }
                        },
                        {
                          "id": "root",
                          "parent": { "type": "workspace" },
                          "properties": {
                            "Name": {
                              "type": "title",
                              "title": [{ "plain_text": "Dandelion Chocolate" }]
                            }
                          }
                        }
                      ],
                      "has_more": true,
                      "next_cursor": "cursor-2"
                    }
                    """)
                : Json(HttpStatusCode.OK, """
                    {
                      "results": [
                        {
                          "id": "page-01",
                          "parent": { "type": "page_id", "page_id": "root" },
                          "properties": {
                            "Name": {
                              "type": "title",
                              "title": [{ "plain_text": "01_Company_Profile" }]
                            }
                          }
                        },
                        {
                          "id": "row-1",
                          "parent": { "type": "database_id", "database_id": "database-1" },
                          "properties": {
                            "Name": {
                              "type": "title",
                              "title": [{ "plain_text": "Database row" }]
                            }
                          }
                        },
                        {
                          "id": "page-00",
                          "parent": { "type": "page_id", "page_id": "root" },
                          "properties": {
                            "Name": {
                              "type": "title",
                              "title": [{ "plain_text": "00_Start_Here" }]
                            }
                          }
                        }
                      ],
                      "has_more": false,
                      "next_cursor": null
                    }
                    """);
        });
        var service = CreateService(dbContext, handler);

        var result = await service.ListResourcesAsync(
            customer.Id, project.Id, source.Id, null, null, CancellationToken.None);

        Assert.Null(result.NextCursor);
        Assert.Equal(
            ["Dandelion Chocolate", "00_Start_Here", "01_Company_Profile", "07_Machine_Ingestion_Guide"],
            result.Resources.Select(value => value.Title));
        Assert.Null(result.Resources[0].ParentId);
        Assert.Equal("root", result.Resources[1].ParentId);
        Assert.Equal(0, result.Resources[1].SortIndex);
        Assert.Equal(1, result.Resources[2].SortIndex);
        Assert.True(result.Resources[2].Selected);
        Assert.DoesNotContain(result.Resources, value => value.Kind == "database_row");
        Assert.DoesNotContain("start_cursor", handler.Requests[0].Body);
        Assert.Contains("\"start_cursor\":\"cursor-2\"", handler.Requests[1].Body);
        Assert.Equal("/proxy/v1/blocks/root/children", handler.Requests[2].Uri.AbsolutePath);
        Assert.Empty(await dbContext.NangoSyncedRecords.ToListAsync());
        Assert.Equal("""["page-01"]""", source.SelectedExternalIdsJson);
    }

    [Fact]
    public async Task Saving_the_first_notion_scope_updates_metadata_and_starts_its_schedule()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(dbContext, project, status: "awaiting_scope");
        var handler = new RecordingHandler(request => request.Uri.AbsolutePath switch
        {
            "/proxy/v1/search" => Json(HttpStatusCode.OK, """
                {
                  "results": [{
                    "id": "page-1",
                    "parent": { "type": "workspace" },
                    "properties": {
                      "Name": {
                        "type": "title",
                        "title": [{ "plain_text": "ERP discovery" }]
                      }
                    }
                  }],
                  "has_more": false
                }
                """),
            "/connections/metadata" => Json(HttpStatusCode.OK, "{}"),
            "/sync/start" => Json(HttpStatusCode.OK, "{}"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        var service = CreateService(dbContext, handler);

        var result = await service.UpdateSelectionAsync(
            customer.Id,
            project.Id,
            source.Id,
            new UpdateInputSourceSelectionInputModel(
                "selected_roots",
                ["page-1"],
                ["page-2"],
                IncludeDescendants: false,
                IncludeNewContent: false),
            CancellationToken.None);

        Assert.Equal("syncing", result.Status);
        Assert.Equal(1, result.SelectedResourceCount);
        Assert.False(result.RequiresSelection);
        Assert.Equal("""["page-1"]""", source.SelectedExternalIdsJson);
        Assert.Equal("[]", source.ExcludedExternalIdsJson);
        Assert.True(source.IncludeDescendants);
        Assert.True(source.IncludeNewContent);
        Assert.Contains(handler.Requests, value =>
            value.Method == HttpMethod.Patch &&
            value.Uri.AbsolutePath == "/connections/metadata" &&
            value.Body.Contains("\"mode\":\"selected_roots\"", StringComparison.Ordinal) &&
            value.Body.Contains("\"includedRootIds\":[\"page-1\"]", StringComparison.Ordinal) &&
            value.Body.Contains("\"excludedIds\":[]", StringComparison.Ordinal) &&
            value.Body.Contains("\"includeDescendants\":true", StringComparison.Ordinal) &&
            value.Body.Contains("\"includeNewContent\":true", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, value =>
            value.Uri.AbsolutePath == "/sync/start" &&
            value.Body.Contains("\"syncs\":[\"documents\"]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paused_schedule_with_a_successful_execution_imports_records_and_self_recovers_without_reauthorization()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(
            dbContext,
            project,
            status: "error",
            selectedExternalIdsJson: """["page-1"]""");
        source.LastError = "The connector synchronization did not complete.";
        source.LastSyncRequestedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await dbContext.SaveChangesAsync();

        var handler = new RecordingHandler(request => request.Uri.AbsolutePath switch
        {
            "/connection" => Json(HttpStatusCode.OK, $$"""
                {
                  "connections": [{
                    "provider_config_key": "notion",
                    "connection_id": "{{source.ConnectionId}}"
                  }]
                }
                """),
            "/sync/status" => Json(HttpStatusCode.OK, $$"""
                {
                  "syncs": [{
                    "status": "PAUSED",
                    "latestExecutionStatus": "SUCCESS",
                    "finishedAt": "{{DateTimeOffset.UtcNow.AddMinutes(1):O}}"
                  }]
                }
                """),
            "/records" => Json(HttpStatusCode.OK, """
                {
                  "records": [{
                    "id": "page-1",
                    "provider": "notion",
                    "title": "ERP discovery",
                    "text": "Inventory is controlled in spreadsheets.",
                    "contentState": "content_extracted"
                  }],
                  "next_cursor": null
                }
                """),
            "/sync/start" => Json(HttpStatusCode.OK, """{"success":true}"""),
            "/sync/trigger" => throw new InvalidOperationException("Recovery must not reset the successful cached sync."),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        var service = CreateService(dbContext, handler);

        var recovered = await service.ReconcileAsync(customer.Id, project.Id, CancellationToken.None);

        Assert.Equal("ready", Assert.Single(recovered.Sources).Status);
        Assert.Single(await dbContext.NangoSyncedRecords.ToListAsync());
        Assert.Contains(handler.Requests, value => value.Uri.AbsolutePath == "/sync/start");
        Assert.Null(source.LastError);
        Assert.Equal(1, source.LastRecordCount);
        Assert.Equal(1, source.LastTextRecordCount);
        Assert.Single(await dbContext.NangoSyncedRecords.ToListAsync());
        Assert.DoesNotContain(handler.Requests, value => value.Uri.AbsolutePath == "/sync/trigger");
    }

    [Fact]
    public async Task Successful_execution_older_than_the_requested_sync_is_not_imported()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(
            dbContext,
            project,
            status: "syncing",
            selectedExternalIdsJson: """["page-1"]""");
        var requestedAt = DateTimeOffset.UtcNow;
        source.LastSyncRequestedAt = requestedAt;
        await dbContext.SaveChangesAsync();
        var handler = new RecordingHandler(request => request.Uri.AbsolutePath switch
        {
            "/connection" => Json(HttpStatusCode.OK, $$"""
                {
                  "connections": [{
                    "provider_config_key": "notion",
                    "connection_id": "{{source.ConnectionId}}"
                  }]
                }
                """),
            "/sync/status" => Json(HttpStatusCode.OK, $$"""
                {
                  "syncs": [{
                    "status": "PAUSED",
                    "latestExecutionStatus": "SUCCESS",
                    "finishedAt": "{{requestedAt.AddMinutes(-1):O}}"
                  }]
                }
                """),
            "/records" => throw new InvalidOperationException("Stale cached records must not be imported."),
            "/sync/start" => throw new InvalidOperationException("The requested execution has not completed."),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        var service = CreateService(dbContext, handler);

        var result = await service.ReconcileAsync(customer.Id, project.Id, CancellationToken.None);

        Assert.Equal("syncing", Assert.Single(result.Sources).Status);
        Assert.Empty(await dbContext.NangoSyncedRecords.ToListAsync());
    }

    [Fact]
    public async Task Completed_sync_imports_normalized_records_and_sets_a_bounded_retention_deadline()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(
            dbContext,
            project,
            status: "syncing",
            selectedExternalIdsJson: """["page-1","page-2"]""");
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("/records", request.Uri.AbsolutePath);
            return Json(HttpStatusCode.OK, """
                {
                  "records": [
                    {
                      "id": "page-1",
                      "provider": "notion",
                      "title": "ERP discovery",
                      "text": "Inventory is controlled in spreadsheets.",
                      "contentState": "content_extracted",
                      "sourceUrl": "https://notion.so/page-1",
                      "modifiedAt": "2026-07-20T12:00:00Z",
                      "metadata": { "selectionScoped": true }
                    },
                    {
                      "id": "page-2",
                      "provider": "notion",
                      "title": "Empty page",
                      "text": "",
                      "contentState": "metadata_only"
                    }
                  ],
                  "next_cursor": null
                }
                """);
        });
        var before = DateTimeOffset.UtcNow.AddDays(30);
        var service = CreateService(dbContext, handler, retentionDays: 30);

        var imported = await service.ImportConnectedEvidenceAsync(
            customer.Id, project.Id, CancellationToken.None);

        Assert.Equal(2, imported);
        Assert.Equal("ready", source.Status);
        Assert.Equal(2, source.LastRecordCount);
        Assert.Equal(1, source.LastTextRecordCount);
        Assert.NotNull(source.RetainContentUntil);
        Assert.InRange(source.RetainContentUntil!.Value, before, DateTimeOffset.UtcNow.AddDays(30).AddMinutes(1));
        var records = await dbContext.NangoSyncedRecords.OrderBy(value => value.ExternalId).ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal("Inventory is controlled in spreadsheets.", records[0].Content);
        Assert.DoesNotContain("Inventory is controlled", records[0].SafeMetadataJson);
        Assert.NotEqual(records[0].Content, records[0].ContentSha256);
    }

    [Fact]
    public async Task Import_paginates_every_record_without_the_previous_five_hundred_record_ceiling()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(
            dbContext,
            project,
            status: "syncing",
            selectedExternalIdsJson: """["root"]""");
        var pageNumber = 0;
        var handler = new RecordingHandler(_ =>
        {
            var currentPage = pageNumber++;
            var records = Enumerable.Range(currentPage * 100, 100)
                .Select(index => new
                {
                    id = $"notion:page-{index}",
                    provider = "notion",
                    title = $"Page {index}",
                    text = $"Evidence {index}",
                    contentState = "content_extracted"
                });
            return Json(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
            {
                records,
                next_cursor = currentPage < 5 ? $"cursor-{currentPage + 1}" : null
            }));
        });
        var service = CreateService(dbContext, handler);

        var imported = await service.ImportConnectedEvidenceAsync(
            customer.Id, project.Id, CancellationToken.None);

        Assert.Equal(600, imported);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(600, source.LastRecordCount);
        Assert.Equal(600, source.SyncDiscoveredCount);
        Assert.Equal(600, await dbContext.NangoSyncedRecords.CountAsync());
    }

    [Fact]
    public async Task Import_removes_records_marked_deleted_by_current_nango_metadata()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(
            dbContext,
            project,
            status: "syncing",
            selectedExternalIdsJson: """["page-1"]""");
        dbContext.NangoSyncedRecords.Add(new NangoSyncedRecord
        {
            GenerationProjectInputSource = source,
            ExternalId = "notion:page-1",
            RecordModel = "UnifiedDocument",
            Provider = "notion",
            Title = "Removed page",
            Content = "Old evidence",
            ContentSha256 = "hash",
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            {
              "records": [{
                "id": "notion:page-1",
                "_nango_metadata": {
                  "last_action": "DELETED",
                  "deleted_at": "2026-07-26T12:00:00Z"
                }
              }],
              "next_cursor": null
            }
            """));
        var service = CreateService(dbContext, handler);

        await service.ImportConnectedEvidenceAsync(
            customer.Id, project.Id, CancellationToken.None);

        Assert.Empty(await dbContext.NangoSyncedRecords.ToListAsync());
        Assert.Equal(0, source.LastRecordCount);
        Assert.Equal("no_records", source.Status);
    }

    [Fact]
    public async Task Disconnect_removes_the_remote_connection_and_all_local_connector_content()
    {
        await using var dbContext = CreateContext();
        var (customer, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(dbContext, project, status: "ready");
        dbContext.NangoSyncedRecords.Add(new NangoSyncedRecord
        {
            GenerationProjectInputSource = source,
            ExternalId = "page-1",
            RecordModel = "UnifiedDocument",
            Provider = "notion",
            Title = "ERP discovery",
            Content = "Evidence",
            ContentSha256 = "hash",
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.NoContent, string.Empty));
        var service = CreateService(dbContext, handler);

        await service.DisconnectAsync(customer.Id, project.Id, source.Id, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/connections/{source.ConnectionId}", request.Uri.AbsolutePath);
        Assert.Empty(await dbContext.GenerationProjectInputSources.ToListAsync());
        Assert.Empty(await dbContext.NangoSyncedRecords.ToListAsync());
    }

    [Fact]
    public async Task Retention_cleanup_deletes_expired_content_but_keeps_the_connection_refreshable()
    {
        await using var dbContext = CreateContext();
        var (_, project) = await SeedProjectAsync(dbContext);
        var source = await SeedSourceAsync(dbContext, project, status: "ready");
        source.RetainContentUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
        source.LastRecordCount = 1;
        source.LastTextRecordCount = 1;
        dbContext.NangoSyncedRecords.Add(new NangoSyncedRecord
        {
            GenerationProjectInputSource = source,
            ExternalId = "page-1",
            RecordModel = "UnifiedDocument",
            Provider = "notion",
            Title = "ERP discovery",
            Content = "Evidence",
            ContentSha256 = "hash",
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}")));

        var removed = await service.PurgeExpiredContentAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Empty(await dbContext.NangoSyncedRecords.ToListAsync());
        Assert.Single(await dbContext.GenerationProjectInputSources.ToListAsync());
        Assert.Equal("expired", source.Status);
        Assert.Equal(0, source.LastRecordCount);
        Assert.Null(source.RetainContentUntil);
    }

    private static ErpGeneratorDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ErpGeneratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static NangoInputSourceService CreateService(
        ErpGeneratorDbContext dbContext,
        HttpMessageHandler handler,
        int retentionDays = 30,
        string connectionWebhookUrl = "") =>
        new(
            dbContext,
            new HttpClient(handler) { BaseAddress = new Uri("https://api.nango.test") },
            Options.Create(new NangoOptions
            {
                BaseUrl = "https://api.nango.test",
                ConnectBaseUrl = "https://connect.nango.test",
                SecretKey = "test-secret",
                Environment = "test",
                AllowedFileIntegrations = "notion",
                ConnectionWebhookUrl = connectionWebhookUrl,
                ConnectorContentRetentionDays = retentionDays
            }));

    private static async Task<(CustomerAccount Customer, GenerationProject Project)> SeedProjectAsync(
        ErpGeneratorDbContext dbContext)
    {
        var customer = new CustomerAccount
        {
            Email = "owner@example.test",
            NormalizedEmail = "OWNER@EXAMPLE.TEST",
            UserName = "owner@example.test",
            NormalizedUserName = "OWNER@EXAMPLE.TEST",
            Name = "Owner",
            PasswordHash = "unused"
        };
        var project = new GenerationProject
        {
            Customer = customer,
            CompanyName = "Dandelion Chocolate",
            Goal = "Replace paper and spreadsheets"
        };
        dbContext.AddRange(customer, project);
        await dbContext.SaveChangesAsync();
        return (customer, project);
    }

    private static async Task<GenerationProjectInputSource> SeedSourceAsync(
        ErpGeneratorDbContext dbContext,
        GenerationProject project,
        string status,
        string selectedExternalIdsJson = "[]")
    {
        var source = new GenerationProjectInputSource
        {
            GenerationProject = project,
            ProviderConfigKey = "notion",
            ConnectionId = $"connection-{Guid.NewGuid():N}",
            SourceKind = "Notion",
            Status = status,
            ScopeMode = selectedExternalIdsJson == "[]" ? "unconfigured" : "selected_roots",
            SelectedExternalIdsJson = selectedExternalIdsJson,
            AuthorizationAcceptedAt = DateTimeOffset.UtcNow,
            AuthorizationNoticeVersion = "connector-evidence-v1"
        };
        dbContext.GenerationProjectInputSources.Add(source);
        await dbContext.SaveChangesAsync();
        return source;
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(
        Func<RecordedRequest, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Request URI is missing."),
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(recorded);
            return responder(recorded);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);
}
