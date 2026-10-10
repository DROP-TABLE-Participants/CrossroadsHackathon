import assert from "node:assert/strict";
import { test } from "node:test";
import { normalizeDocument } from "../src/shared/document-normalizer.js";

test("normalizer strips obvious secrets from text and metadata", () => {
  const result = normalizeDocument({
    id: "doc-1",
    title: "  Process Notes  ",
    provider: "google-drive",
    text: "Line one\r\n\r\n\r\nAuthorization: Bearer raw-token\napi_key=secret-value",
    metadata: {
      access_token: "raw-token",
      safeKey: "safe-value",
      callbackUrl: "https://example.test/callback?token=secret-value"
    }
  });

  assert.equal(result.document.title, "Process Notes");
  assert.match(result.document.text, /\*\*\*masked\*\*\*/);
  assert.doesNotMatch(JSON.stringify(result.document), /raw-token|secret-value/);
  assert.equal(result.document.metadata?.safeKey, "safe-value");
  assert.equal(result.document.metadata?.callbackUrl, "https://example.test/callback?***masked***");
  assert.equal("access_token" in (result.document.metadata ?? {}), false);
});

test("normalizer emits warning and safe empty text for empty content", () => {
  const result = normalizeDocument({
    id: "empty",
    title: "Empty.txt",
    provider: "sharepoint",
    text: ""
  });

  assert.equal(result.document.text, "");
  assert.equal(result.warnings.length, 1);
  assert.equal(result.warnings[0]?.code, "empty_text");
});

test("normalizer is deterministic for the same input", () => {
  const input = {
    id: "doc-2",
    title: "Stable.md",
    provider: "google-drive",
    text: "Stable content",
    metadata: { size: 42 }
  } as const;

  assert.deepEqual(normalizeDocument(input), normalizeDocument(input));
});
