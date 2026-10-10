import type {
  UnifiedDocument,
  UnifiedDocumentContentState
} from "../../models/unified-document.js";
import {
  type DocumentMappingBatch,
  type DocumentNormalizationResult,
  normalizeDocument
} from "../../shared/document-normalizer.js";
import { safeProviderMetadata } from "../../shared/provider-metadata.js";

export type GoogleDriveOwner = {
  displayName?: string;
  emailAddress?: string;
};

export type GoogleDriveFileFixture = {
  id: string;
  name: string;
  mimeType?: string;
  webViewLink?: string;
  modifiedTime?: string;
  owners?: GoogleDriveOwner[];
  text?: string;
  preview?: string;
  size?: string;
  md5Checksum?: string;
  capabilities?: {
    canDownload?: boolean;
  };
  contentState?: UnifiedDocumentContentState;
  contentExtractionMethod?: string;
  contentExtractionMimeType?: string;
  contentTruncated?: boolean;
  access_token?: string;
};

export type NangoDocumentSyncRuntime = {
  get<TResponse = unknown>(request: {
    endpoint: string;
    params?: string | Record<string, string | number | string[] | number[]> | undefined;
    headers?: Record<string, string> | undefined;
  }): Promise<{ data: TResponse }>;
  paginate?: unknown;
  batchSave?: unknown;
};

type GoogleDriveFilesResponse = {
  files?: GoogleDriveFileFixture[];
  nextPageToken?: string;
};

type GoogleDriveContentExtraction = {
  text: string;
  contentState: UnifiedDocumentContentState;
  method?: "export" | "download";
  outputMimeType?: string;
  truncated?: boolean;
};

const GOOGLE_DOCUMENT = "application/vnd.google-apps.document";
const GOOGLE_SPREADSHEET = "application/vnd.google-apps.spreadsheet";
const GOOGLE_PRESENTATION = "application/vnd.google-apps.presentation";
const GOOGLE_APPS_PREFIX = "application/vnd.google-apps.";
const DEFAULT_MAX_CONTENT_CHARACTERS = 2_000_000;

export function mapGoogleDriveFileToUnifiedDocument(
  file: GoogleDriveFileFixture
): DocumentNormalizationResult {
  return normalizeDocument({
    id: file.id,
    title: file.name,
    text: file.text || file.preview || "",
    contentState: file.contentState ??
      (file.text || file.preview ? "content_extracted" : "metadata_only"),
    sourceUrl: file.webViewLink,
    provider: "google-drive",
    mimeType: file.mimeType,
    modifiedAt: file.modifiedTime,
    author: file.owners?.[0]?.displayName ?? file.owners?.[0]?.emailAddress,
    metadata: {
      ...safeProviderMetadata(file as Record<string, unknown>, [
        "size",
        "md5Checksum",
        "mimeType",
        "contentExtractionMethod",
        "contentExtractionMimeType",
        "contentTruncated"
      ]),
      ownerEmail: file.owners?.[0]?.emailAddress
    }
  });
}

export function mapGoogleDriveFilesToUnifiedDocuments(
  files: readonly GoogleDriveFileFixture[]
): DocumentMappingBatch {
  const documents: UnifiedDocument[] = [];
  const warnings: DocumentMappingBatch["warnings"] = [];

  for (const file of files) {
    const result = mapGoogleDriveFileToUnifiedDocument(file);
    documents.push(result.document);
    warnings.push(...result.warnings);
  }

  return { documents, warnings };
}

export async function syncGoogleDriveDocuments(
  nango: NangoDocumentSyncRuntime,
  maxDocuments = 25,
  maxContentCharacters = DEFAULT_MAX_CONTENT_CHARACTERS
): Promise<UnifiedDocument[]> {
  const documents: UnifiedDocument[] = [];
  let pageToken: string | undefined;

  do {
    const params: Record<string, string | number | string[] | number[]> = {
      pageSize: Math.min(25, Math.max(1, maxDocuments - documents.length)),
      q: "trashed = false",
      fields: "nextPageToken,files(id,name,mimeType,webViewLink,modifiedTime,owners(displayName,emailAddress),size,md5Checksum,capabilities(canDownload))",
      supportsAllDrives: "true",
      includeItemsFromAllDrives: "true"
    };

    if (pageToken) {
      params["pageToken"] = pageToken;
    }

    const response = await nango.get<GoogleDriveFilesResponse>({
      endpoint: "/drive/v3/files",
      params
    });

    const files = Array.isArray(response.data.files) ? response.data.files : [];
    const extractedFiles: GoogleDriveFileFixture[] = [];
    for (const file of files) {
      const extraction = await extractGoogleDriveFileContent(nango, file, maxContentCharacters);
      extractedFiles.push({
        ...file,
        text: extraction.text,
        contentState: extraction.contentState,
        ...(extraction.method === undefined ? {} : { contentExtractionMethod: extraction.method }),
        ...(extraction.outputMimeType === undefined ? {} : { contentExtractionMimeType: extraction.outputMimeType }),
        ...(extraction.truncated === undefined ? {} : { contentTruncated: extraction.truncated })
      });
    }

    const mapped = mapGoogleDriveFilesToUnifiedDocuments(extractedFiles);
    documents.push(...mapped.documents);
    pageToken = response.data.nextPageToken;
  }
  while (pageToken && documents.length < maxDocuments);

  return documents.slice(0, maxDocuments);
}

export async function extractGoogleDriveFileContent(
  nango: NangoDocumentSyncRuntime,
  file: GoogleDriveFileFixture,
  maxContentCharacters = DEFAULT_MAX_CONTENT_CHARACTERS
): Promise<GoogleDriveContentExtraction> {
  const embeddedText = file.text || file.preview;
  if (embeddedText) {
    return boundedExtraction(embeddedText, "content_extracted", maxContentCharacters);
  }

  if (file.capabilities?.canDownload === false) {
    return { text: "", contentState: "unsupported" };
  }

  const request = contentRequest(file);
  if (request === undefined) {
    return { text: "", contentState: "unsupported" };
  }

  try {
    const response = await nango.get<unknown>({
      endpoint: request.endpoint,
      params: request.params,
      ...(request.method === "download"
        ? { headers: { Range: `bytes=0-${Math.max(0, maxContentCharacters * 4 - 1)}` } }
        : {})
    });
    const text = responseText(response.data);
    if (text.length === 0) {
      return {
        text: "",
        contentState: "extraction_failed",
        method: request.method,
        outputMimeType: request.outputMimeType
      };
    }

    return boundedExtraction(
      text,
      "content_extracted",
      maxContentCharacters,
      request.method,
      request.outputMimeType
    );
  }
  catch {
    return {
      text: "",
      contentState: "extraction_failed",
      method: request.method,
      outputMimeType: request.outputMimeType
    };
  }
}

function contentRequest(file: GoogleDriveFileFixture): {
  endpoint: string;
  params: Record<string, string>;
  method: "export" | "download";
  outputMimeType: string;
} | undefined {
  const mimeType = (file.mimeType ?? "").toLowerCase();
  let exportMimeType: string | undefined;
  if (mimeType === GOOGLE_DOCUMENT || mimeType === GOOGLE_PRESENTATION) {
    exportMimeType = "text/plain";
  }
  else if (mimeType === GOOGLE_SPREADSHEET) {
    exportMimeType = "text/csv";
  }

  if (exportMimeType !== undefined) {
    return {
      endpoint: `/drive/v3/files/${encodeURIComponent(file.id)}/export`,
      params: { mimeType: exportMimeType },
      method: "export",
      outputMimeType: exportMimeType
    };
  }

  if (mimeType.startsWith(GOOGLE_APPS_PREFIX) || !isDownloadableTextFile(file)) {
    return undefined;
  }

  return {
    endpoint: `/drive/v3/files/${encodeURIComponent(file.id)}`,
    params: { alt: "media" },
    method: "download",
    outputMimeType: mimeType || "text/plain"
  };
}

function isDownloadableTextFile(file: GoogleDriveFileFixture): boolean {
  const mimeType = (file.mimeType ?? "").toLowerCase();
  if (mimeType.startsWith("text/") ||
      mimeType === "application/json" ||
      mimeType === "application/xml" ||
      mimeType.endsWith("+json") ||
      mimeType.endsWith("+xml")) {
    return true;
  }

  const extension = file.name.toLowerCase().split(".").pop();
  return extension !== undefined && [
    "txt", "md", "markdown", "csv", "tsv", "json", "xml", "yaml", "yml", "log"
  ].includes(extension);
}

function responseText(data: unknown): string {
  if (typeof data === "string") {
    return data;
  }

  if (data instanceof Uint8Array) {
    return new TextDecoder("utf-8", { fatal: false }).decode(data);
  }

  if (data instanceof ArrayBuffer) {
    return new TextDecoder("utf-8", { fatal: false }).decode(new Uint8Array(data));
  }

  if (data !== null && typeof data === "object") {
    return JSON.stringify(data);
  }

  return "";
}

function boundedExtraction(
  text: string,
  contentState: UnifiedDocumentContentState,
  maxContentCharacters: number,
  method?: "export" | "download",
  outputMimeType?: string
): GoogleDriveContentExtraction {
  const safeLimit = Math.max(1, maxContentCharacters);
  const truncated = text.length > safeLimit;
  return {
    text: truncated ? text.slice(0, safeLimit) : text,
    contentState,
    ...(method === undefined ? {} : { method }),
    ...(outputMimeType === undefined ? {} : { outputMimeType }),
    ...(truncated ? { truncated: true } : {})
  };
}
