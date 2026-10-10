import type {
  UnifiedDocument,
  UnifiedDocumentContentState
} from "../models/unified-document.js";
import { addMetadataValue } from "./provider-metadata.js";
import { normalizeText, normalizeTitle } from "./safe-text.js";

export type DocumentNormalizationWarning = {
  code: "empty_text" | "missing_id";
  message: string;
  recordId?: string;
};

export type DocumentNormalizationInput = {
  id?: string;
  title?: string;
  text?: string | undefined;
  contentState?: UnifiedDocumentContentState | undefined;
  sourceUrl?: string | undefined;
  provider: "google-drive" | "sharepoint" | string;
  mimeType?: string | undefined;
  modifiedAt?: string | undefined;
  author?: string | undefined;
  metadata?: Record<string, unknown>;
};

export type DocumentNormalizationResult = {
  document: UnifiedDocument;
  warnings: DocumentNormalizationWarning[];
};

export type DocumentMappingBatch = {
  documents: UnifiedDocument[];
  warnings: DocumentNormalizationWarning[];
};

export function normalizeDocument(input: DocumentNormalizationInput): DocumentNormalizationResult {
  const warnings: DocumentNormalizationWarning[] = [];
  const id = normalizeText(input.id, 300);
  if (id.length === 0) {
    warnings.push({
      code: "missing_id",
      message: "Provider document did not include an id; using deterministic fallback."
    });
  }

  const documentId = id.length > 0 ? id : stableFallbackId(input.title, input.sourceUrl);
  const normalizedText = normalizeText(input.text);
  const requestedContentState = input.contentState ??
    (normalizedText.length > 0 ? "content_extracted" : "metadata_only");
  const contentState = requestedContentState === "content_extracted" && normalizedText.length === 0
    ? "metadata_only"
    : requestedContentState;
  const text = contentState === "content_extracted" ? normalizedText : "";
  if (text.length === 0) {
    warnings.push({
      code: "empty_text",
      message: `Provider document '${documentId}' did not include extractable text.`,
      recordId: documentId
    });
  }

  const metadata = cleanMetadata(input.metadata ?? {});
  const document: UnifiedDocument = {
    id: documentId,
    title: normalizeTitle(input.title, documentId),
    text,
    contentState,
    provider: input.provider
  };

  assignIfPresent(document, "sourceUrl", input.sourceUrl);
  assignIfPresent(document, "mimeType", input.mimeType);
  assignIfPresent(document, "modifiedAt", input.modifiedAt);
  assignIfPresent(document, "author", input.author);
  if (Object.keys(metadata).length > 0) {
    document.metadata = metadata;
  }

  return { document, warnings };
}

function cleanMetadata(input: Record<string, unknown>): Record<string, unknown> {
  const metadata: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(input)) {
    addMetadataValue(metadata, key, value);
  }

  return metadata;
}

function assignIfPresent<T extends object, K extends keyof T>(
  target: T,
  key: K,
  value: T[K] | undefined
): void {
  if (value !== undefined && value !== "") {
    target[key] = value;
  }
}

function stableFallbackId(title?: string, sourceUrl?: string): string {
  const seed = normalizeText(`${title ?? "document"}:${sourceUrl ?? "unknown"}`, 500);
  let hash = 0;
  for (const character of seed) {
    hash = (hash * 31 + character.charCodeAt(0)) >>> 0;
  }

  return `document-${hash.toString(16).padStart(8, "0")}`;
}
