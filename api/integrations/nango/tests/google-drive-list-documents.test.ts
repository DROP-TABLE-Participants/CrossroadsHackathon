import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import {
  extractGoogleDriveFileContent,
  type GoogleDriveFileFixture,
  mapGoogleDriveFilesToUnifiedDocuments,
  syncGoogleDriveDocuments
} from "../src/files/google-drive/list-documents.js";
import type { UnifiedDocument } from "../src/models/unified-document.js";

test("Google Drive fixture maps to deterministic UnifiedDocument output", () => {
  const files = readJson<GoogleDriveFileFixture[]>("src/files/google-drive/fixtures/google-drive-files.json");
  const expected = readJson<UnifiedDocument[]>("tests/fixtures/expected-unified-documents.json")
    .filter((document) => document.provider === "google-drive");

  const first = mapGoogleDriveFilesToUnifiedDocuments(files);
  const second = mapGoogleDriveFilesToUnifiedDocuments(files);

  assert.deepEqual(first.documents, expected);
  assert.deepEqual(first.documents, second.documents);
  assert.deepEqual(first.warnings.map((warning) => warning.code), ["empty_text"]);
});

test("Google Drive UnifiedDocument fields are present and safe", () => {
  const files = readJson<GoogleDriveFileFixture[]>("src/files/google-drive/fixtures/google-drive-files.json");
  const { documents } = mapGoogleDriveFilesToUnifiedDocuments(files);

  for (const document of documents) {
    assertRequiredUnifiedDocumentShape(document);
    assert.equal(document.provider, "google-drive");
    assert.ok(document.sourceUrl?.startsWith("https://drive.google.com/"));
    assert.doesNotMatch(JSON.stringify(document), /drive-secret-value|google-drive-secret-token/);
  }
});

test("Google Workspace documents and text blobs are fetched as bounded evidence", async () => {
  const requests: Array<{ endpoint: string; params?: unknown; headers?: unknown }> = [];
  const files: GoogleDriveFileFixture[] = [
    { id: "doc-1", name: "Procedure", mimeType: "application/vnd.google-apps.document" },
    { id: "sheet-1", name: "BOM", mimeType: "application/vnd.google-apps.spreadsheet" },
    { id: "text-1", name: "notes.md", mimeType: "text/markdown" }
  ];
  const nango = {
    async get<TResponse>(request: { endpoint: string; params?: unknown; headers?: unknown }): Promise<{ data: TResponse }> {
      requests.push(request);
      if (request.endpoint === "/drive/v3/files") {
        return { data: { files } as TResponse };
      }

      const data = request.endpoint.includes("sheet-1")
        ? "sku,quantity\nA,5"
        : request.endpoint.includes("text-1")
          ? "text file evidence"
          : "procedure evidence that must be truncated";
      return { data: data as TResponse };
    }
  };

  const documents = await syncGoogleDriveDocuments(nango, 3, 24);

  assert.equal(documents.length, 3);
  assert.ok(documents.every((document) => document.contentState === "content_extracted"));
  assert.equal(documents[0]?.text, "procedure evidence that");
  assert.equal(documents[0]?.metadata?.["contentTruncated"], true);
  assert.equal(requests[1]?.endpoint, "/drive/v3/files/doc-1/export");
  assert.deepEqual(requests[1]?.params, { mimeType: "text/plain" });
  assert.equal(requests[2]?.endpoint, "/drive/v3/files/sheet-1/export");
  assert.deepEqual(requests[2]?.params, { mimeType: "text/csv" });
  assert.equal(requests[3]?.endpoint, "/drive/v3/files/text-1");
  assert.deepEqual(requests[3]?.params, { alt: "media" });
});

test("unsupported and failed Google Drive extraction never fabricates document text", async () => {
  const unsupported = await extractGoogleDriveFileContent(
    {
      get: async <TResponse>() => ({ data: "must not be called" as TResponse })
    },
    { id: "pdf-1", name: "invoice.pdf", mimeType: "application/pdf" }
  );
  let attempted = false;
  const failed = await extractGoogleDriveFileContent(
    {
      get: async () => {
        attempted = true;
        throw new Error("provider failure with sensitive details");
      }
    },
    { id: "text-2", name: "notes.txt", mimeType: "text/plain" }
  );

  assert.deepEqual(unsupported, { text: "", contentState: "unsupported" });
  assert.equal(attempted, true);
  assert.equal(failed.text, "");
  assert.equal(failed.contentState, "extraction_failed");
  assert.doesNotMatch(JSON.stringify(failed), /sensitive details/);
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
