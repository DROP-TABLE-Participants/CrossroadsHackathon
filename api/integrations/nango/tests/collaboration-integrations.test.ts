import assert from "node:assert/strict";
import { test } from "node:test";
import { syncNotionDocuments } from "../src/files/notion/list-documents.js";
import { mapTeamsMessage, syncTeamsMessages } from "../src/messages/microsoft-teams/list-messages.js";
import { mapSlackMessage, syncSlackMessages } from "../src/messages/slack/list-messages.js";

test("Slack messages map to safe deterministic UnifiedMessage records", async () => {
  const runtime = {
    async get<TResponse>(request: { endpoint: string }): Promise<{ data: TResponse }> {
      if (request.endpoint === "/conversations.list") {
        return { data: { channels: [{ id: "C1", name: "operations" }] } as TResponse };
      }
      return { data: { messages: [{ ts: "1710000000.000100", user: "U1", text: "Approve the order before release." }] } as TResponse };
    }
  };

  const messages = await syncSlackMessages(runtime);
  assert.equal(messages.length, 1);
  assert.deepEqual(messages[0], mapSlackMessage(
    { ts: "1710000000.000100", user: "U1", text: "Approve the order before release." },
    { id: "C1", name: "operations" }
  ));
  assert.equal(messages[0]?.provider, "slack");
  assert.equal(messages[0]?.channelOrThread, "operations");
});

test("Microsoft Teams channel messages are discovered and HTML is removed", async () => {
  const endpoints: string[] = [];
  const runtime = {
    async get<TResponse>(request: { endpoint: string }): Promise<{ data: TResponse }> {
      endpoints.push(request.endpoint);
      if (request.endpoint === "/v1.0/me/joinedTeams") return response({ value: [{ id: "T1", displayName: "Factory" }] });
      if (request.endpoint === "/v1.0/teams/T1/channels") return response({ value: [{ id: "C1", displayName: "Quality" }] });
      return response({ value: [{
        id: "M1",
        createdDateTime: "2026-07-15T08:00:00Z",
        from: { user: { displayName: "Ada" } },
        body: { contentType: "html", content: "<p>Release <strong>lot 42</strong>.</p>" }
      }] });
    }
  };

  const messages = await syncTeamsMessages(runtime);
  assert.equal(messages.length, 1);
  assert.equal(messages[0]?.text, "Release lot 42 .");
  assert.equal(messages[0]?.channelOrThread, "Factory/Quality");
  assert.ok(endpoints.includes("/v1.0/teams/T1/channels/C1/messages"));
  assert.deepEqual(messages[0], mapTeamsMessage({
    id: "M1",
    createdDateTime: "2026-07-15T08:00:00Z",
    from: { user: { displayName: "Ada" } },
    body: { contentType: "html", content: "<p>Release <strong>lot 42</strong>.</p>" }
  }, { id: "T1", displayName: "Factory" }, { id: "C1", displayName: "Quality" }));
});

test("Notion selected roots include nested content under the Portal scope contract", async () => {
  const page = {
    id: "page-1",
    object: "page",
    url: "https://notion.so/page-1",
    last_edited_time: "2026-07-15T08:00:00Z",
    properties: {
      Name: { type: "title", title: [{ plain_text: "Order approval" }] },
      Approver: { type: "rich_text", rich_text: [{ plain_text: "Finance Director" }] },
      Priority: { type: "select", select: { name: "Must" } },
      "API token": { type: "rich_text", rich_text: [{ plain_text: "must-not-leak" }] }
    }
  };
  const parentBlock = {
    id: "block-1",
    type: "paragraph",
    has_children: true,
    paragraph: { rich_text: [{ plain_text: "Finance approves orders above 10,000 EUR." }] }
  };
  const childBlock = {
    id: "block-2",
    type: "bulleted_list_item",
    bulleted_list_item: { rich_text: [{ plain_text: "Approval happens before release." }] }
  };
  const postCursors: Array<string | undefined> = [];
  const getRequests: Array<{ endpoint: string; params?: Record<string, string | number> }> = [];
  const runtime = {
    async getMetadata<TResponse>(): Promise<TResponse> {
      return {
        notionScope: {
          mode: "selected_roots",
          includedRootIds: ["page-1"],
          excludedIds: [],
          includeDescendants: true,
          includeNewContent: true
        }
      } as TResponse;
    },
    async post<TResponse>(request: { data?: { start_cursor?: string } }): Promise<{ data: TResponse }> {
      postCursors.push(request.data?.start_cursor);
      return response({ results: [page], has_more: false });
    },
    async get<TResponse>(request: {
      endpoint: string;
      params?: Record<string, string | number>;
    }): Promise<{ data: TResponse }> {
      getRequests.push(request);
      if (request.endpoint === "/v1/users/me") return response({ bot: { workspace_id: "workspace-test" } });
      if (request.endpoint === "/v1/pages/page-1") return response(page);
      if (request.endpoint === "/v1/blocks/page-1/children") {
        return response({ results: [parentBlock], has_more: false });
      }
      if (request.endpoint === "/v1/blocks/block-1/children") {
        return response({ results: [childBlock], has_more: false });
      }
      throw new Error(`Unexpected Notion request: ${request.endpoint}`);
    }
  };

  const documents = await syncNotionDocuments(runtime);
  assert.equal(documents.length, 1);
  assert.equal(documents[0]?.provider, "notion");
  assert.equal(documents[0]?.contentState, "content_extracted");
  assert.equal(documents[0]?.title, "Order approval");
  assert.match(documents[0]?.text ?? "", /Approver: Finance Director/);
  assert.match(documents[0]?.text ?? "", /Priority: Must/);
  assert.match(documents[0]?.text ?? "", /Approval happens before release/);
  assert.doesNotMatch(documents[0]?.text ?? "", /must-not-leak|API token/);
  assert.deepEqual(postCursors, []);
  assert.ok(getRequests.some((request) => request.endpoint === "/v1/blocks/block-1/children"));
});

test("Notion extraction paginates all block children without a content-count ceiling", async () => {
  const page = {
    id: "page-2",
    object: "page",
    properties: { Name: { type: "title", title: [{ plain_text: "Paginated procedure" }] } }
  };
  const cursors: Array<string | undefined> = [];
  const runtime = {
    async getMetadata<TResponse>(): Promise<TResponse> {
      return {
        notionScope: {
          mode: "selected_roots",
          includedRootIds: ["page-2"],
          excludedIds: [],
          includeDescendants: true,
          includeNewContent: true
        }
      } as TResponse;
    },
    async post<TResponse>(): Promise<{ data: TResponse }> {
      throw new Error("Search must not run after a bounded selection is saved.");
    },
    async get<TResponse>(request: {
      endpoint: string;
      params?: Record<string, string | number>;
    }): Promise<{ data: TResponse }> {
      if (request.endpoint === "/v1/users/me") return response({ bot: { workspace_id: "workspace-test" } });
      if (request.endpoint === "/v1/pages/page-2") return response(page);
      cursors.push(request.params?.start_cursor?.toString());
      return request.params?.start_cursor === "cursor-2"
        ? response({
            results: [{
              id: "block-2",
              type: "paragraph",
              paragraph: { rich_text: [{ plain_text: "Second page" }] }
            }],
            has_more: false
          })
        : response({
            results: [{
              id: "block-1",
              type: "paragraph",
              paragraph: { rich_text: [{ plain_text: "First page" }] }
            }],
            has_more: true,
            next_cursor: "cursor-2"
          });
    }
  };

  const documents = await syncNotionDocuments(runtime);

  assert.equal(documents.length, 1);
  assert.match(documents[0]?.text ?? "", /First page/);
  assert.match(documents[0]?.text ?? "", /Second page/);
  assert.deepEqual(cursors, [undefined, "cursor-2"]);
});

test("Notion all-accessible scope paginates every search result and isolates page failures", async () => {
  const searchCursors: Array<string | undefined> = [];
  const runtime = {
    async getMetadata<TResponse>(): Promise<TResponse> {
      return {
        notionScope: {
          mode: "all_accessible",
          includedRootIds: [],
          excludedIds: [],
          includeDescendants: true,
          includeNewContent: true
        }
      } as TResponse;
    },
    async post<TResponse>(request: {
      endpoint: string;
      data?: { start_cursor?: string };
    }): Promise<{ data: TResponse }> {
      assert.equal(request.endpoint, "/v1/search");
      searchCursors.push(request.data?.start_cursor);
      return request.data?.start_cursor
        ? response({
            results: [{
              id: "page-2",
              object: "page",
              properties: { Name: { type: "title", title: [{ plain_text: "Working page" }] } }
            }],
            has_more: false
          })
        : response({
            results: [{
              id: "page-1",
              object: "page",
              properties: { Name: { type: "title", title: [{ plain_text: "Unavailable page" }] } }
            }],
            has_more: true,
            next_cursor: "search-2"
          });
    },
    async get<TResponse>(request: { endpoint: string }): Promise<{ data: TResponse }> {
      if (request.endpoint === "/v1/users/me") return response({ bot: { workspace_id: "workspace-test" } });
      if (request.endpoint === "/v1/blocks/page-1/children") {
        throw new Error("Page access changed during synchronization.");
      }
      if (request.endpoint === "/v1/blocks/page-2/children") {
        return response({
          results: [{
            id: "block-2",
            type: "paragraph",
            paragraph: { rich_text: [{ plain_text: "Still useful evidence" }] }
          }],
          has_more: false
        });
      }
      throw new Error(`Unexpected Notion request: ${request.endpoint}`);
    }
  };

  const documents = await syncNotionDocuments(runtime);

  assert.equal(documents.length, 2);
  assert.equal(documents[0]?.contentState, "extraction_failed");
  assert.equal(documents[1]?.contentState, "content_extracted");
  assert.match(documents[1]?.text ?? "", /Still useful evidence/);
  assert.deepEqual(searchCursors, [undefined, "search-2"]);
});

test("Notion retries preserve selected-page, database-row, and excluded-branch evidence boundaries", async () => {
  const calls: string[] = [];
  const runtime = {
    async getMetadata<TResponse>(): Promise<TResponse> {
      return { notionScope: {
        mode: "selected_roots", includedRootIds: ["root"], excludedIds: ["excluded"],
        includeDescendants: true, includeNewContent: true
      } } as TResponse;
    },
    async get<TResponse>(request: { endpoint: string; retries?: number }): Promise<{ data: TResponse }> {
      calls.push(request.endpoint);
      assert.equal(request.retries, 3);
      switch (request.endpoint) {
        case "/v1/users/me": return response({ bot: { workspace_id: "workspace-test" } });
        case "/v1/pages/root": return response({ id: "root", object: "page", url: "https://notion.so/root",
          properties: { Name: { type: "title", title: [{ plain_text: "Factory" }] } } });
        case "/v1/blocks/root/children": return response({ results: [
          { id: "orders", type: "child_database", child_database: { title: "Orders" } },
          { id: "excluded", type: "child_page", child_page: { title: "Unselected area" } }
        ] });
        case "/v1/databases/orders": return response({ id: "orders", object: "database",
          title: [{ plain_text: "Orders" }], properties: { Name: { type: "title" } } });
        case "/v1/blocks/order-1/children": return response({ results: [
          { type: "paragraph", paragraph: { rich_text: [{ plain_text: "Release quality before shipment." }] } }
        ] });
        default: throw new Error(`Unexpected request outside selected scope: ${request.endpoint}`);
      }
    },
    async post<TResponse>(request: { endpoint: string; retries?: number }): Promise<{ data: TResponse }> {
      calls.push(request.endpoint);
      assert.equal(request.retries, 3);
      assert.equal(request.endpoint, "/v1/databases/orders/query");
      return response({ results: [{ id: "order-1", object: "page", parent: { database_id: "orders" },
        properties: { Name: { type: "title", title: [{ plain_text: "Wholesale order" }] } } }] });
    }
  };

  const documents = await syncNotionDocuments(runtime);
  assert.deepEqual(documents.map(d => [d.id, d.itemKind, d.parentExternalId]), [
    ["root", "page", undefined], ["orders", "database", "root"], ["order-1", "database_row", "orders"]
  ]);
  assert.equal(documents.every(d => d.workspaceId === "workspace-test"), true);
  assert.match(documents.find(d => d.id === "order-1")?.text ?? "", /Release quality before shipment/);
  assert.equal(calls.some(path => path.includes("excluded") || path === "/v1/search"), false);
});

test("Notion sync fails closed until the customer configures a scope", async () => {
  let providerCalled = false;
  const runtime = {
    async getMetadata<TResponse>(): Promise<TResponse> {
      return { selectedNotionPageIds: [] } as TResponse;
    },
    async post<TResponse>(): Promise<{ data: TResponse }> {
      providerCalled = true;
      return response({});
    },
    async get<TResponse>(): Promise<{ data: TResponse }> {
      providerCalled = true;
      return response({});
    }
  };

  assert.deepEqual(await syncNotionDocuments(runtime), []);
  assert.equal(providerCalled, false);
});

function response<TResponse>(data: unknown): { data: TResponse } {
  return { data: data as TResponse };
}
