import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { strToU8, zipSync } from "fflate";
import type { UnifiedDocument } from "../src/models/unified-document.js";
import {
  extractSharePointDriveItemContent,
  mapSharePointDriveItemsToUnifiedDocuments,
  syncSharePointDocuments,
  type NangoDocumentSyncRuntime,
  type SharePointDriveItemFixture
} from "../src/files/sharepoint/list-documents.js";

test("SharePoint fixture maps to deterministic UnifiedDocument output", () => {
  const items = readJson<SharePointDriveItemFixture[]>("src/files/sharepoint/fixtures/sharepoint-files.json");
  const expected = readJson<UnifiedDocument[]>("tests/fixtures/expected-unified-documents.json")
    .filter((document) => document.provider === "sharepoint");

  const first = mapSharePointDriveItemsToUnifiedDocuments(items);
  const second = mapSharePointDriveItemsToUnifiedDocuments(items);

  assert.deepEqual(first.documents, expected);
  assert.deepEqual(first.documents, second.documents);
  assert.deepEqual(first.warnings, []);
});

test("SharePoint UnifiedDocument fields are present and safe", () => {
  const items = readJson<SharePointDriveItemFixture[]>("src/files/sharepoint/fixtures/sharepoint-files.json");
  const { documents } = mapSharePointDriveItemsToUnifiedDocuments(items);

  for (const document of documents) {
    assertRequiredUnifiedDocumentShape(document);
    assert.equal(document.provider, "sharepoint");
    assert.ok(document.sourceUrl?.startsWith("https://contoso.sharepoint.com/"));
    assert.doesNotMatch(JSON.stringify(document), /sharepoint-secret/);
  }
});

test("SharePoint live sync discovers document libraries and extracts bounded text and Office Open XML", async () => {
  const runtime = new FakeSharePointRuntime();

  const documents = await syncSharePointDocuments(runtime, 10, 5000, 500_000);

  assert.equal(documents.length, 3);
  assert.deepEqual(documents.map((document) => [document.id, document.contentState]), [
    ["sharepoint:drive-1:notes-1", "content_extracted"],
    ["sharepoint:drive-1:pdf-1", "unsupported"],
    ["sharepoint:drive-1:procedure-1", "content_extracted"]
  ]);
  assert.match(documents[0]?.text || "", /Finance approves invoices above 10,000 EUR/);
  assert.match(documents[2]?.text || "", /Quality manager releases every production lot/);
  assert.equal(documents[1]?.text, "");
  assert.equal(documents[0]?.metadata?.driveName, "Shared Documents");
  assert.equal(documents[0]?.metadata?.siteName, "Operations");
  assert.ok(runtime.requests.some((request) => request.endpoint === "/sites"));
  assert.ok(runtime.requests.some((request) => request.endpoint === "/sites/site-1/drives"));
  assert.ok(runtime.requests.some((request) => request.endpoint === "/drives/drive-1/root/children"));
  assert.ok(runtime.requests.some((request) => request.endpoint === "/drives/drive-1/items/folder-1/children"));
  assert.ok(runtime.requests.some((request) => request.endpoint === "/drives/drive-1/items/notes-1/content"));
  assert.ok(runtime.requests.some((request) => request.endpoint === "/drives/drive-1/items/procedure-1/content"));
  assert.ok(!runtime.requests.some((request) => request.endpoint === "/drives/drive-1/items/pdf-1/content"));
});

test("SharePoint content extraction fails closed without turning metadata into evidence", async () => {
  const runtime: NangoDocumentSyncRuntime = {
    async get() {
      throw new Error("provider detail that must not become evidence");
    }
  };
  const result = await extractSharePointDriveItemContent(runtime, "drive-1", {
    id: "notes-1",
    name: "notes.txt",
    file: { mimeType: "text/plain" },
    size: 100
  });

  assert.equal(result.contentState, "extraction_failed");
  assert.equal(result.text, "");
});

test("SharePoint content extraction rejects oversized downloads before requesting content", async () => {
  let called = false;
  const runtime: NangoDocumentSyncRuntime = {
    async get() {
      called = true;
      return { data: "must not be read" };
    }
  };
  const result = await extractSharePointDriveItemContent(runtime, "drive-1", {
    id: "large-1",
    name: "large.docx",
    file: { mimeType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
    size: 500_001
  }, 5000, 500_000);

  assert.equal(result.contentState, "unsupported");
  assert.equal(result.text, "");
  assert.equal(called, false);
});

function assertRequiredUnifiedDocumentShape(document: UnifiedDocument): void {
  assert.equal(typeof document.id, "string");
  assert.equal(typeof document.title, "string");
  assert.equal(typeof document.text, "string");
  assert.equal(typeof document.contentState, "string");
  assert.equal(typeof document.provider, "string");
  assert.ok(document.id.length > 0);
  assert.ok(document.title.length > 0);
}

function readJson<T>(relativePath: string): T {
  return JSON.parse(readFileSync(path.join(process.cwd(), relativePath), "utf8")) as T;
}

class FakeSharePointRuntime implements NangoDocumentSyncRuntime {
  public readonly requests: Array<{ endpoint: string; responseType?: string }> = [];

  public async get(request: {
    endpoint: string;
    responseType?: "arraybuffer" | "blob" | "document" | "json" | "text" | "stream";
  }): Promise<{ data: any }> {
    this.requests.push({
      endpoint: request.endpoint,
      ...(request.responseType === undefined ? {} : { responseType: request.responseType })
    });

    if (request.endpoint === "/sites") {
      return response({ value: [{ id: "site-1", displayName: "Operations" }] });
    }
    if (request.endpoint === "/sites/root") {
      return response({ id: "site-1", displayName: "Operations" });
    }
    if (request.endpoint === "/sites/site-1/drives") {
      return response({ value: [{ id: "drive-1", name: "Shared Documents", driveType: "documentLibrary" }] });
    }
    if (request.endpoint === "/drives/drive-1/root/children") {
      return response({
        value: [
          { id: "folder-1", name: "Procedures", folder: { childCount: 1 } },
          {
            id: "notes-1",
            name: "Invoice approvals.txt",
            webUrl: "https://contoso.sharepoint.com/sites/ops/Shared%20Documents/Invoice%20approvals.txt",
            file: { mimeType: "text/plain" },
            size: 120
          },
          {
            id: "pdf-1",
            name: "Scanned contract.pdf",
            webUrl: "https://contoso.sharepoint.com/sites/ops/Shared%20Documents/Scanned%20contract.pdf",
            file: { mimeType: "application/pdf" },
            size: 200
          }
        ]
      });
    }
    if (request.endpoint === "/drives/drive-1/items/folder-1/children") {
      return response({
        value: [{
          id: "procedure-1",
          name: "Quality release.docx",
          webUrl: "https://contoso.sharepoint.com/sites/ops/Shared%20Documents/Procedures/Quality%20release.docx",
          file: { mimeType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
          size: 500
        }]
      });
    }
    if (request.endpoint === "/drives/drive-1/items/notes-1/content") {
      return response("Finance approves invoices above 10,000 EUR before posting.");
    }
    if (request.endpoint === "/drives/drive-1/items/procedure-1/content") {
      return response(zipSync({
        "word/document.xml": strToU8(
          "<w:document><w:body><w:p><w:r><w:t>Quality manager releases every production lot.</w:t></w:r></w:p></w:body></w:document>"
        )
      }));
    }

    throw new Error(`Unexpected Graph request: ${request.endpoint}`);
  }
}

function response<TResponse>(data: TResponse): { data: TResponse } {
  return { data };
}
