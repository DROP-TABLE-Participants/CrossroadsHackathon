import { Open } from "unzipper";
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

export type SharePointIdentity = {
  displayName?: string;
  email?: string;
};

export type SharePointDriveItemFixture = {
  id: string;
  name: string;
  webUrl?: string;
  lastModifiedDateTime?: string;
  createdBy?: {
    user?: SharePointIdentity;
  };
  file?: {
    mimeType?: string;
  };
  folder?: {
    childCount?: number;
  };
  size?: number;
  eTag?: string;
  text?: string;
  preview?: string;
  contentState?: UnifiedDocumentContentState;
  contentExtractionMethod?: string;
  contentExtractionMimeType?: string;
  contentTruncated?: boolean;
  documentId?: string;
  driveId?: string;
  driveName?: string;
  siteId?: string;
  siteName?: string;
  downloadUrl?: string;
};

export type NangoDocumentSyncRuntime = {
  // Nango's runner keeps this method generic and Axios-compatible. Keeping the
  // boundary broad here lets fixture fakes stay tiny while all requests built
  // below remain typed and allowlisted.
  get(request: any): Promise<{ data: any }>;
  paginate?: unknown;
  batchSave?: unknown;
};

type GraphCollection<T> = {
  value?: T[];
  "@odata.nextLink"?: string;
};

type SharePointSite = {
  id: string;
  displayName?: string;
  name?: string;
  webUrl?: string;
};

type SharePointDrive = {
  id: string;
  name?: string;
  webUrl?: string;
  driveType?: string;
};

type SharePointContentExtraction = {
  text: string;
  contentState: UnifiedDocumentContentState;
  method?: "download" | "ooxml";
  outputMimeType?: string;
  truncated?: boolean;
};

const DEFAULT_MAX_CONTENT_CHARACTERS = 1_000_000;
const DEFAULT_MAX_DOWNLOAD_BYTES = 8_000_000;
const DEFAULT_MAX_DOCUMENTS = 25;
const DEFAULT_MAX_SITES = 10;
const DEFAULT_MAX_FOLDERS = 100;
const DEFAULT_MAX_GRAPH_PAGES = 5;
const MAX_RELEVANT_ARCHIVE_BYTES = 24_000_000;

export function mapSharePointDriveItemToUnifiedDocument(
  item: SharePointDriveItemFixture
): DocumentNormalizationResult {
  return normalizeDocument({
    id: item.documentId ?? item.id,
    title: item.name,
    text: item.text ?? item.preview ?? "",
    contentState: item.contentState ??
      (item.text || item.preview ? "content_extracted" : "metadata_only"),
    sourceUrl: item.webUrl,
    provider: "sharepoint",
    mimeType: item.file?.mimeType,
    modifiedAt: item.lastModifiedDateTime,
    author: item.createdBy?.user?.displayName ?? item.createdBy?.user?.email,
    metadata: {
      ...safeProviderMetadata(item as Record<string, unknown>, [
        "size",
        "eTag",
        "driveId",
        "driveName",
        "siteId",
        "siteName",
        "contentExtractionMethod",
        "contentExtractionMimeType",
        "contentTruncated"
      ]),
      authorEmail: item.createdBy?.user?.email
    }
  });
}

export function mapSharePointDriveItemsToUnifiedDocuments(
  items: readonly SharePointDriveItemFixture[]
): DocumentMappingBatch {
  const documents: UnifiedDocument[] = [];
  const warnings: DocumentMappingBatch["warnings"] = [];

  for (const item of items) {
    const result = mapSharePointDriveItemToUnifiedDocument(item);
    documents.push(result.document);
    warnings.push(...result.warnings);
  }

  return { documents, warnings };
}

export async function syncSharePointDocuments(
  nango: NangoDocumentSyncRuntime,
  maxDocuments = DEFAULT_MAX_DOCUMENTS,
  maxContentCharacters = DEFAULT_MAX_CONTENT_CHARACTERS,
  maxDownloadBytes = DEFAULT_MAX_DOWNLOAD_BYTES
): Promise<UnifiedDocument[]> {
  const safeDocumentLimit = Math.max(1, maxDocuments);
  const sites = await discoverSharePointSites(nango);
  const extractedItems: SharePointDriveItemFixture[] = [];
  let listedDriveCount = 0;

  for (const site of sites) {
    if (extractedItems.length >= safeDocumentLimit) break;

    let drives: SharePointDrive[];
    try {
      drives = await readGraphCollection<SharePointDrive>(
        nango,
        `/sites/${encodeURIComponent(site.id)}/drives`,
        { "$select": "id,name,webUrl,driveType", "$top": 25 }
      );
    }
    catch {
      continue;
    }

    for (const drive of drives) {
      if (extractedItems.length >= safeDocumentLimit) break;
      if (!drive.id) continue;

      const remaining = safeDocumentLimit - extractedItems.length;
      try {
        const items = await listDriveDocuments(
          nango,
          site,
          drive,
          remaining,
          maxContentCharacters,
          maxDownloadBytes
        );
        extractedItems.push(...items);
        listedDriveCount += 1;
      }
      catch {
        // A single inaccessible document library must not turn content from
        // other authorized libraries into fabricated evidence.
      }
    }
  }

  if (listedDriveCount === 0) {
    throw new Error("SharePoint document libraries could not be listed with the authorized connection.");
  }

  return mapSharePointDriveItemsToUnifiedDocuments(extractedItems.slice(0, safeDocumentLimit)).documents;
}

export async function extractSharePointDriveItemContent(
  nango: NangoDocumentSyncRuntime,
  driveId: string,
  item: SharePointDriveItemFixture,
  maxContentCharacters = DEFAULT_MAX_CONTENT_CHARACTERS,
  maxDownloadBytes = DEFAULT_MAX_DOWNLOAD_BYTES
): Promise<SharePointContentExtraction> {
  const embeddedText = item.text || item.preview;
  if (embeddedText) {
    return boundedExtraction(embeddedText, maxContentCharacters);
  }

  if (!item.file || !isExtractableFile(item)) {
    return { text: "", contentState: "unsupported" };
  }

  if (typeof item.size !== "number" || item.size < 0 || item.size > Math.max(1, maxDownloadBytes)) {
    return { text: "", contentState: "unsupported" };
  }

  const outputMimeType = item.file.mimeType || mimeTypeFromName(item.name) || "application/octet-stream";
  const officeOpenXml = isOfficeOpenXml(item);
  try {
    const response = await nango.get({
      endpoint: `/drives/${encodeURIComponent(driveId)}/items/${encodeURIComponent(item.id)}/content`,
      responseType: "arraybuffer",
      ...(officeOpenXml
        ? {}
        : { headers: { Range: `bytes=0-${Math.max(0, maxDownloadBytes - 1)}` } })
    });
    const bytes = responseBytes(response.data);
    if (bytes.byteLength === 0 || bytes.byteLength > Math.max(1, maxDownloadBytes)) {
      return {
        text: "",
        contentState: bytes.byteLength > maxDownloadBytes ? "unsupported" : "extraction_failed",
        method: officeOpenXml ? "ooxml" : "download",
        outputMimeType
      };
    }

    const text = officeOpenXml
      ? await extractOfficeOpenXmlText(bytes, item.name)
      : decodeDownloadedText(bytes, outputMimeType);
    if (!text.trim()) {
      return {
        text: "",
        contentState: "extraction_failed",
        method: officeOpenXml ? "ooxml" : "download",
        outputMimeType
      };
    }

    return boundedExtraction(
      text,
      maxContentCharacters,
      officeOpenXml ? "ooxml" : "download",
      outputMimeType
    );
  }
  catch {
    return {
      text: "",
      contentState: "extraction_failed",
      method: officeOpenXml ? "ooxml" : "download",
      outputMimeType
    };
  }
}

async function discoverSharePointSites(nango: NangoDocumentSyncRuntime): Promise<SharePointSite[]> {
  const discovered: SharePointSite[] = [];
  try {
    discovered.push(...await readGraphCollection<SharePointSite>(
      nango,
      "/me/followedSites",
      { "$select": "id,displayName,name,webUrl", "$top": DEFAULT_MAX_SITES }
    ));
  }
  catch {
    // Followed sites are the delegated, least-broad discovery path but are not
    // supported for every Microsoft account/tenant configuration.
  }

  try {
    discovered.push(...await readGraphCollection<SharePointSite>(
      nango,
      "/sites",
      { search: "*", "$select": "id,displayName,name,webUrl", "$top": DEFAULT_MAX_SITES }
    ));
  }
  catch {
    // Some delegated tenants do not allow broad site search. The root site
    // remains a least-surprise fallback for an explicitly authorized tenant.
  }

  try {
    const root = await nango.get({
      endpoint: "/sites/root",
      params: { "$select": "id,displayName,name,webUrl" }
    });
    const rootSite = root.data as SharePointSite;
    if (rootSite?.id) discovered.push(rootSite);
  }
  catch {
    // The search result can still be sufficient when root-site lookup is not.
  }

  const unique = new Map<string, SharePointSite>();
  for (const site of discovered) {
    if (site?.id && !unique.has(site.id)) unique.set(site.id, site);
  }

  const sites = [...unique.values()].slice(0, DEFAULT_MAX_SITES);
  if (sites.length === 0) {
    throw new Error("SharePoint sites could not be discovered with the authorized connection.");
  }

  return sites;
}

async function listDriveDocuments(
  nango: NangoDocumentSyncRuntime,
  site: SharePointSite,
  drive: SharePointDrive,
  maxDocuments: number,
  maxContentCharacters: number,
  maxDownloadBytes: number
): Promise<SharePointDriveItemFixture[]> {
  const documents: SharePointDriveItemFixture[] = [];
  const folders: Array<string | undefined> = [undefined];
  let visitedFolders = 0;

  while (folders.length > 0 && documents.length < maxDocuments && visitedFolders < DEFAULT_MAX_FOLDERS) {
    const folderId = folders.shift();
    visitedFolders += 1;
    const endpoint = folderId
      ? `/drives/${encodeURIComponent(drive.id)}/items/${encodeURIComponent(folderId)}/children`
      : `/drives/${encodeURIComponent(drive.id)}/root/children`;
    const items = await readGraphCollection<SharePointDriveItemFixture>(
      nango,
      endpoint,
      {
        "$select": "id,name,webUrl,lastModifiedDateTime,createdBy,file,folder,size,eTag",
        "$top": 200
      }
    );

    for (const item of items) {
      if (item.folder) {
        if (item.id) folders.push(item.id);
        continue;
      }
      if (!item.file || !item.id || !item.name) continue;

      const extraction = await extractSharePointDriveItemContent(
        nango,
        drive.id,
        item,
        maxContentCharacters,
        maxDownloadBytes
      );
      documents.push({
        ...item,
        documentId: `sharepoint:${drive.id}:${item.id}`,
        text: extraction.text,
        contentState: extraction.contentState,
        driveId: drive.id,
        siteId: site.id,
        ...(drive.name === undefined ? {} : { driveName: drive.name }),
        ...((site.displayName || site.name) === undefined ? {} : { siteName: site.displayName || site.name }),
        ...(extraction.method === undefined ? {} : { contentExtractionMethod: extraction.method }),
        ...(extraction.outputMimeType === undefined ? {} : { contentExtractionMimeType: extraction.outputMimeType }),
        ...(extraction.truncated === undefined ? {} : { contentTruncated: extraction.truncated })
      });
      if (documents.length >= maxDocuments) break;
    }
  }

  return documents;
}

async function readGraphCollection<T>(
  nango: NangoDocumentSyncRuntime,
  endpoint: string,
  params?: Record<string, string | number | string[] | number[]>
): Promise<T[]> {
  const values: T[] = [];
  let nextEndpoint: string | undefined = endpoint;
  let nextParams = params;
  let pages = 0;

  while (nextEndpoint && pages < DEFAULT_MAX_GRAPH_PAGES) {
    const response = await nango.get({
      endpoint: nextEndpoint,
      ...(nextParams === undefined ? {} : { params: nextParams })
    });
    const data = response.data as GraphCollection<T>;
    if (Array.isArray(data?.value)) values.push(...data.value);
    nextEndpoint = safeGraphNextEndpoint(data?.["@odata.nextLink"]);
    nextParams = undefined;
    pages += 1;
  }

  return values;
}

function safeGraphNextEndpoint(value: string | undefined): string | undefined {
  if (!value) return undefined;
  try {
    const url = new URL(value, "https://graph.microsoft.com");
    if (url.hostname !== "graph.microsoft.com") return undefined;
    const path = url.pathname.replace(/^\/v1\.0(?=\/)/i, "");
    return `${path}${url.search}`;
  }
  catch {
    return undefined;
  }
}

function isExtractableFile(item: SharePointDriveItemFixture): boolean {
  if (isOfficeOpenXml(item)) return true;
  const mimeType = (item.file?.mimeType || "").toLowerCase();
  if (mimeType.startsWith("text/") ||
      mimeType === "application/json" ||
      mimeType === "application/xml" ||
      mimeType.endsWith("+json") ||
      mimeType.endsWith("+xml")) {
    return true;
  }

  const extension = fileExtension(item.name);
  return [
    "txt", "md", "markdown", "csv", "tsv", "json", "xml", "yaml", "yml", "log", "html", "htm"
  ].includes(extension);
}

function isOfficeOpenXml(item: SharePointDriveItemFixture): boolean {
  const extension = fileExtension(item.name);
  return ["docx", "docm", "pptx", "pptm", "xlsx", "xlsm"].includes(extension);
}

async function extractOfficeOpenXmlText(bytes: Uint8Array, name: string): Promise<string> {
  const archive = await readRelevantOfficeArchive(bytes);
  const extension = fileExtension(name);

  if (extension === "docx" || extension === "docm") {
    const document = archive["word/document.xml"];
    return document ? extractXmlText(decodeUtf8(document)) : "";
  }

  if (extension === "pptx" || extension === "pptm") {
    return Object.entries(archive)
      .filter(([path]) => /^ppt\/slides\/slide\d+\.xml$/i.test(path))
      .sort(([left], [right]) => naturalPathCompare(left, right))
      .map(([, content]) => extractXmlText(decodeUtf8(content)))
      .filter(Boolean)
      .join("\n\n");
  }

  if (extension === "xlsx" || extension === "xlsm") {
    return extractSpreadsheetText(archive);
  }

  return "";
}

async function readRelevantOfficeArchive(bytes: Uint8Array): Promise<Record<string, Uint8Array>> {
  const source = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const directory = await Open.buffer(source);
  const archive: Record<string, Uint8Array> = {};
  let totalBytes = 0;

  for (const file of directory.files) {
    if (file.type !== "File" || !isRelevantOfficeArchivePath(file.path)) continue;
    totalBytes += file.uncompressedSize;
    if (file.uncompressedSize < 0 || totalBytes > MAX_RELEVANT_ARCHIVE_BYTES) {
      throw new Error("Office archive expands beyond the extraction limit.");
    }

    const content = await file.buffer();
    archive[file.path] = new Uint8Array(content.buffer, content.byteOffset, content.byteLength);
  }

  return archive;
}

function isRelevantOfficeArchivePath(path: string): boolean {
  return path === "word/document.xml" ||
    /^ppt\/slides\/slide\d+\.xml$/i.test(path) ||
    path === "xl/sharedStrings.xml" ||
    /^xl\/worksheets\/sheet\d+\.xml$/i.test(path);
}

function extractSpreadsheetText(archive: Record<string, Uint8Array>): string {
  const sharedStringXml = archive["xl/sharedStrings.xml"];
  const sharedStrings = sharedStringXml
    ? [...decodeUtf8(sharedStringXml).matchAll(/<si(?:\s[^>]*)?>([\s\S]*?)<\/si>/gi)]
        .map((match) => extractXmlText(match[1] || ""))
    : [];
  const rows: string[] = [];

  for (const [, content] of Object.entries(archive)
    .filter(([entry]) => /^xl\/worksheets\/sheet\d+\.xml$/i.test(entry))
    .sort(([left], [right]) => naturalPathCompare(left, right))) {
    const xml = decodeUtf8(content);
    for (const rowMatch of xml.matchAll(/<row(?:\s[^>]*)?>([\s\S]*?)<\/row>/gi)) {
      const cells: string[] = [];
      for (const cellMatch of (rowMatch[1] || "").matchAll(/<c([^>]*)>([\s\S]*?)<\/c>/gi)) {
        const attributes = cellMatch[1] || "";
        const body = cellMatch[2] || "";
        const value = body.match(/<v(?:\s[^>]*)?>([\s\S]*?)<\/v>/i)?.[1];
        if (/\bt=["']s["']/i.test(attributes) && value !== undefined) {
          const index = Number.parseInt(value, 10);
          cells.push(Number.isInteger(index) ? sharedStrings[index] || "" : "");
        }
        else if (/\bt=["']inlineStr["']/i.test(attributes)) {
          cells.push(extractXmlText(body));
        }
        else if (value !== undefined) {
          cells.push(decodeXmlEntities(value));
        }
      }
      if (cells.some((cell) => cell.trim())) rows.push(cells.join("\t"));
    }
    if (rows.length > 0) rows.push("");
  }

  return rows.join("\n").trim();
}

function extractXmlText(xml: string): string {
  const paragraphs = xml
    .replace(/<\/(?:w:p|a:p|row|si)>/gi, "\n")
    .matchAll(/<(?:w:t|a:t|t)(?:\s[^>]*)?>([\s\S]*?)<\/(?:w:t|a:t|t)>/gi);
  return [...paragraphs]
    .map((match) => decodeXmlEntities(match[1] || ""))
    .join(" ")
    .replace(/[ \t]+\n/g, "\n")
    .replace(/\n[ \t]+/g, "\n")
    .replace(/[ \t]{2,}/g, " ")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

function decodeDownloadedText(bytes: Uint8Array, mimeType: string): string {
  const decoded = new TextDecoder("utf-8", { fatal: false }).decode(bytes);
  if (mimeType.toLowerCase().includes("html")) {
    return decodeXmlEntities(decoded.replace(/<script[\s\S]*?<\/script>/gi, " ")
      .replace(/<style[\s\S]*?<\/style>/gi, " ")
      .replace(/<[^>]+>/g, " "));
  }
  return decoded;
}

function responseBytes(data: unknown): Uint8Array {
  if (data instanceof Uint8Array) return data;
  if (data instanceof ArrayBuffer) return new Uint8Array(data);
  if (typeof data === "string") return new TextEncoder().encode(data);
  if (data !== null && typeof data === "object") {
    return new TextEncoder().encode(JSON.stringify(data));
  }
  return new Uint8Array();
}

function decodeUtf8(bytes: Uint8Array): string {
  return new TextDecoder("utf-8", { fatal: false }).decode(bytes);
}

function boundedExtraction(
  text: string,
  maxContentCharacters: number,
  method?: "download" | "ooxml",
  outputMimeType?: string
): SharePointContentExtraction {
  const safeLimit = Math.max(1, maxContentCharacters);
  const truncated = text.length > safeLimit;
  return {
    text: truncated ? text.slice(0, safeLimit) : text,
    contentState: "content_extracted",
    ...(method === undefined ? {} : { method }),
    ...(outputMimeType === undefined ? {} : { outputMimeType }),
    ...(truncated ? { truncated: true } : {})
  };
}

function fileExtension(name: string): string {
  return name.toLowerCase().split(".").pop() || "";
}

function mimeTypeFromName(name: string): string | undefined {
  const extension = fileExtension(name);
  if (["txt", "md", "markdown", "csv", "tsv", "yaml", "yml", "log"].includes(extension)) return "text/plain";
  if (extension === "json") return "application/json";
  if (extension === "xml") return "application/xml";
  if (extension === "html" || extension === "htm") return "text/html";
  return undefined;
}

function naturalPathCompare(left: string, right: string): number {
  return left.localeCompare(right, undefined, { numeric: true, sensitivity: "base" });
}

function decodeXmlEntities(value: string): string {
  return value
    .replace(/&#x([0-9a-f]+);/gi, (_, hex: string) => String.fromCodePoint(Number.parseInt(hex, 16)))
    .replace(/&#(\d+);/g, (_, decimal: string) => String.fromCodePoint(Number.parseInt(decimal, 10)))
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, "\"")
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&");
}
