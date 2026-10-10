import type { UnifiedDocument } from "../../models/unified-document.js";
import { normalizeDocument } from "../../shared/document-normalizer.js";

export type NotionSyncMetadata = {
  notionScope?: {
    mode?: "all_accessible" | "selected_roots";
    includedRootIds?: string[];
    excludedIds?: string[];
    includeDescendants?: boolean;
    includeNewContent?: boolean;
  };
};

export type NotionSyncRuntime = {
  get<TResponse>(request: {
    endpoint: string;
    retries?: number;
    params?: Record<string, string | number>;
  }): Promise<{ data: TResponse }>;
  post<TResponse>(request: {
    endpoint: string;
    retries?: number;
    data?: Record<string, unknown>;
  }): Promise<{ data: TResponse }>;
  getMetadata<TMetadata>(): Promise<TMetadata>;
};

type RichText = { plain_text?: string };

type NotionUser = { id?: string; name?: string };

type NotionParent = {
  type?: string;
  page_id?: string;
  database_id?: string;
  data_source_id?: string;
  block_id?: string;
  workspace?: boolean;
};

type NotionProperty = {
  type?: string;
  title?: RichText[];
  rich_text?: RichText[];
  number?: number | null;
  select?: { name?: string } | null;
  status?: { name?: string } | null;
  multi_select?: Array<{ name?: string }>;
  date?: { start?: string; end?: string | null } | null;
  formula?: { type?: string; string?: string | null; number?: number | null; boolean?: boolean | null; date?: { start?: string; end?: string | null } | null };
  rollup?: { type?: string; number?: number | null; date?: { start?: string; end?: string | null } | null; array?: NotionProperty[] };
  checkbox?: boolean;
  url?: string | null;
  email?: string | null;
  phone_number?: string | null;
  people?: NotionUser[];
  relation?: Array<{ id?: string }>;
  files?: Array<{ name?: string }>;
  created_time?: string;
  last_edited_time?: string;
  created_by?: NotionUser;
  last_edited_by?: NotionUser;
};

export type NotionPage = {
  id?: string;
  object?: string;
  url?: string;
  created_time?: string;
  last_edited_time?: string;
  parent?: NotionParent;
  properties?: Record<string, NotionProperty>;
  title?: RichText[];
};

type NotionDatabase = NotionPage & { object?: "database" | "data_source" };

export type NotionBlock = {
  id?: string;
  type?: string;
  has_children?: boolean;
  [key: string]: unknown;
};

type NotionListResponse<T> = { results?: T[]; has_more?: boolean; next_cursor?: string | null };
type NotionBotResponse = { bot?: { workspace_id?: string } };

type ReadBlocksResult = { blocks: NotionBlock[]; childPageIds: string[]; childDatabaseIds: string[] };

export type NotionSyncCheckpoint = {
  scopeKey: string;
  searchCursor: string;
  rootIndex: number;
  processed: number;
  failed: number;
};

type NotionSyncOptions = {
  batchSize?: number;
  checkpoint?: NotionSyncCheckpoint | null;
  onBatch?: (documents: UnifiedDocument[]) => Promise<void>;
  onCheckpoint?: (checkpoint: NotionSyncCheckpoint) => Promise<void>;
};

type CanonicalScope = {
  mode: "all_accessible" | "selected_roots";
  includedRootIds: string[];
  excludedIds: string[];
  includeDescendants: boolean;
  includeNewContent: boolean;
};

const sensitivePropertyName = /(password|passcode|secret|token|api[\s_-]?key|private[\s_-]?key|credit[\s_-]?card|card[\s_-]?number|cvv|cvc|iban|bank[\s_-]?account|salary|payroll|medical|health|diagnosis|social[\s_-]?security|ssn)/i;

export async function syncNotionDocuments(nango: NotionSyncRuntime): Promise<UnifiedDocument[]> {
  const documents: UnifiedDocument[] = [];
  await streamNotionDocuments(nango, { onBatch: async (batch) => { documents.push(...batch); } });
  return documents;
}

export async function streamNotionDocuments(
  nango: NotionSyncRuntime,
  options: NotionSyncOptions = {}
): Promise<{ processed: number; failed: number }> {
  const configured = (await nango.getMetadata<NotionSyncMetadata>()).notionScope;
  const scope = canonicalScope(configured);
  if (!scope) return { processed: 0, failed: 0 };
  const activeScope = scope;

  const workspaceId = await resolveWorkspaceId(nango);
  const scopeKey = JSON.stringify(activeScope);
  const saved = options.checkpoint?.scopeKey === scopeKey ? options.checkpoint : null;
  let processed = saved?.processed ?? 0;
  let failed = saved?.failed ?? 0;
  const batchSize = Math.max(1, options.batchSize ?? 25);
  const batch: UnifiedDocument[] = [];
  const visited = new Set<string>();
  const excluded = new Set(scope.excludedIds);

  async function emit(document: UnifiedDocument): Promise<void> {
    batch.push(document);
    processed++;
    if (document.contentState === "extraction_failed") failed++;
    if (batch.length >= batchSize) await flush();
  }

  async function flush(): Promise<void> {
    if (batch.length === 0) return;
    await options.onBatch?.(batch.splice(0, batch.length));
  }

  async function processPage(pageId: string, knownPage?: NotionPage, parentExternalId?: string): Promise<void> {
    const canonicalId = canonicalNotionId(pageId);
    if (!canonicalId || visited.has(canonicalId) || excluded.has(canonicalId)) return;
    visited.add(canonicalId);
    try {
      const page = knownPage ?? (await nango.get<NotionPage>({ retries: 3, endpoint: `/v1/pages/${encodeURIComponent(canonicalId)}` })).data;
      const blocks = await readNotionBlocks(nango, canonicalId, activeScope.includeDescendants, excluded);
      await emit(mapNotionPageToUnifiedDocument(page, blocks.blocks, {
        workspaceId,
        parentExternalId: parentExternalId ?? resolveParentId(page.parent),
        itemKind: "page"
      }));
      if (!activeScope.includeDescendants) return;
      for (const childPageId of blocks.childPageIds) {
        await processPage(childPageId, undefined, canonicalId);
      }
      for (const childDatabaseId of blocks.childDatabaseIds) {
        await processDatabase(childDatabaseId, canonicalId);
      }
    } catch (error) {
      if (error instanceof Error && error.message === "notion_api_version_contract_mismatch") throw error;
      await emit(unavailableDocument(canonicalId, workspaceId, parentExternalId, "page", activeScope.mode));
    }
  }

  async function processDatabase(databaseId: string, parentExternalId?: string): Promise<void> {
    const canonicalId = canonicalNotionId(databaseId);
    if (!canonicalId || visited.has(canonicalId) || excluded.has(canonicalId)) return;
    visited.add(canonicalId);
    try {
      const database = (await nango.get<NotionDatabase>({ retries: 3, endpoint: `/v1/databases/${encodeURIComponent(canonicalId)}` })).data;
      assertDatabaseObjectContract(database);
      await emit(mapNotionDatabaseToUnifiedDocument(database, {
        workspaceId,
        parentExternalId: parentExternalId ?? resolveParentId(database.parent)
      }));
      if (!activeScope.includeDescendants) return;
      let cursor: string | undefined;
      do {
        const response = (await nango.post<NotionListResponse<NotionPage>>({ retries: 3,
          endpoint: `/v1/databases/${encodeURIComponent(canonicalId)}/query`,
          data: { page_size: 100, ...(cursor ? { start_cursor: cursor } : {}) }
        })).data;
        for (const row of response.results ?? []) {
          if (!row.id) continue;
          await processDatabaseRow(row, canonicalId);
        }
        cursor = response.has_more && response.next_cursor ? response.next_cursor : undefined;
      } while (cursor);
    } catch (error) {
      if (error instanceof Error && error.message === "notion_api_version_contract_mismatch") throw error;
      await emit(unavailableDocument(canonicalId, workspaceId, parentExternalId, "database", activeScope.mode));
    }
  }

  async function processDatabaseRow(row: NotionPage, databaseId: string): Promise<void> {
    const canonicalId = canonicalNotionId(row.id);
    if (!canonicalId || visited.has(canonicalId) || excluded.has(canonicalId)) return;
    visited.add(canonicalId);
    try {
      const blocks = await readNotionBlocks(nango, canonicalId, activeScope.includeDescendants, excluded);
      await emit(mapNotionPageToUnifiedDocument(row, blocks.blocks, {
        workspaceId,
        parentExternalId: databaseId,
        itemKind: "database_row"
      }));
      if (!activeScope.includeDescendants) return;
      for (const childPageId of blocks.childPageIds) {
        await processPage(childPageId, undefined, canonicalId);
      }
      for (const childDatabaseId of blocks.childDatabaseIds) {
        await processDatabase(childDatabaseId, canonicalId);
      }
    } catch (error) {
      if (error instanceof Error && error.message === "notion_api_version_contract_mismatch") throw error;
      await emit(unavailableDocument(canonicalId, workspaceId, databaseId, "database_row", activeScope.mode));
    }
  }

  async function processSearchItem(item: NotionPage): Promise<void> {
    const itemId = canonicalNotionId(item.id);
    if (!itemId || excluded.has(itemId)) return;
    if (item.object === "database" || item.object === "data_source") {
      await processDatabase(itemId, resolveParentId(item.parent));
    } else {
      await processPage(itemId, item, resolveParentId(item.parent));
    }
  }

  if (activeScope.mode === "all_accessible") {
    let cursor = saved?.searchCursor || undefined;
    do {
      const response = (await nango.post<NotionListResponse<NotionPage>>({ retries: 3,
        endpoint: "/v1/search",
        data: { page_size: 100, ...(cursor ? { start_cursor: cursor } : {}) }
      })).data;
      for (const item of response.results ?? []) await processSearchItem(item);
      await flush();
      cursor = response.has_more && response.next_cursor ? response.next_cursor : undefined;
      await options.onCheckpoint?.({ scopeKey, searchCursor: cursor ?? "", rootIndex: -1, processed, failed });
    } while (cursor);
  } else {
    for (let index = saved && saved.rootIndex >= 0 ? saved.rootIndex : 0; index < activeScope.includedRootIds.length; index++) {
      const rootId = activeScope.includedRootIds[index]!;
      if (excluded.has(rootId)) {
        await options.onCheckpoint?.({ scopeKey, searchCursor: "", rootIndex: index + 1, processed, failed });
        continue;
      }
      let page: NotionPage;
      try {
        page = (await nango.get<NotionPage>({ retries: 3, endpoint: `/v1/pages/${encodeURIComponent(rootId)}` })).data;
      } catch {
        await processDatabase(rootId);
        await flush();
        await options.onCheckpoint?.({ scopeKey, searchCursor: "", rootIndex: index + 1, processed, failed });
        continue;
      }
      await processPage(rootId, page);
      await flush();
      await options.onCheckpoint?.({ scopeKey, searchCursor: "", rootIndex: index + 1, processed, failed });
    }
  }

  await flush();
  return { processed, failed };
}

export async function readNotionBlocks(
  nango: Pick<NotionSyncRuntime, "get">,
  pageId: string,
  includeDescendants = true,
  excludedIds: ReadonlySet<string> = new Set()
): Promise<ReadBlocksResult> {
  const blocks: NotionBlock[] = [];
  const childPageIds: string[] = [];
  const childDatabaseIds: string[] = [];
  const visited = new Set<string>();

  async function visit(parentId: string): Promise<void> {
    if (visited.has(parentId)) return;
    visited.add(parentId);
    let cursor: string | undefined;
    do {
      const response = (await nango.get<NotionListResponse<NotionBlock>>({ retries: 3,
        endpoint: `/v1/blocks/${encodeURIComponent(parentId)}/children`,
        params: { page_size: 100, ...(cursor ? { start_cursor: cursor } : {}) }
      })).data;
      for (const block of response.results ?? []) {
        if (block.id && excludedIds.has(canonicalNotionId(block.id))) continue;
        blocks.push(block);
        if (block.type === "child_page" && block.id) {
          childPageIds.push(canonicalNotionId(block.id));
          continue;
        }
        if (block.type === "child_database" && block.id) {
          childDatabaseIds.push(canonicalNotionId(block.id));
          continue;
        }
        if (includeDescendants && block.has_children && block.id) await visit(canonicalNotionId(block.id));
      }
      cursor = response.has_more && response.next_cursor ? response.next_cursor : undefined;
    } while (cursor);
  }

  await visit(pageId);
  return { blocks, childPageIds: uniqueSorted(childPageIds), childDatabaseIds: uniqueSorted(childDatabaseIds) };
}

export function mapNotionPageToUnifiedDocument(
  page: NotionPage,
  blocks: NotionBlock[],
  context: { workspaceId: string; parentExternalId?: string | undefined; itemKind: "page" | "database_row" }
): UnifiedDocument {
  const title = notionPageTitle(page);
  const propertyLines = notionPropertyLines(page.properties ?? {});
  const blockLines = blocks.flatMap(blockText).filter((value) => value.trim().length > 0);
  const text = [
    propertyLines.length > 0 ? `Properties\n${propertyLines.join("\n")}` : "",
    blockLines.length > 0 ? `Page content\n${blockLines.join("\n")}` : ""
  ].filter(Boolean).join("\n\n");
  return typedNotionDocument({
    id: canonicalNotionId(page.id),
    title,
    text,
    contentState: text ? "content_extracted" : "metadata_only",
    sourceUrl: page.url,
    modifiedAt: page.last_edited_time,
    createdAt: page.created_time,
    workspaceId: context.workspaceId,
    parentExternalId: context.parentExternalId,
    itemKind: context.itemKind,
    metadata: { object: page.object, blockCount: blocks.length, propertyCount: propertyLines.length, selectionScoped: true }
  });
}

export function mapNotionDatabaseToUnifiedDocument(
  database: NotionDatabase,
  context: { workspaceId: string; parentExternalId?: string | undefined }
): UnifiedDocument {
  const title = richText(database.title) || notionPageTitle(database);
  const schema = Object.entries(database.properties ?? {}).map(([name, property]) => `${name}: ${property.type ?? "unknown"}`);
  const text = [`Database: ${title}`, schema.length > 0 ? `Properties\n${schema.join("\n")}` : ""].filter(Boolean).join("\n\n");
  return typedNotionDocument({
    id: canonicalNotionId(database.id),
    title,
    text,
    contentState: "content_extracted",
    sourceUrl: database.url,
    modifiedAt: database.last_edited_time,
    createdAt: database.created_time,
    workspaceId: context.workspaceId,
    parentExternalId: context.parentExternalId,
    itemKind: "database",
    metadata: { object: database.object, propertyCount: schema.length, selectionScoped: true }
  });
}

function typedNotionDocument(input: {
  id: string;
  title: string;
  text: string;
  contentState: UnifiedDocument["contentState"];
  sourceUrl?: string | undefined;
  modifiedAt?: string | undefined;
  createdAt?: string | undefined;
  workspaceId: string;
  parentExternalId?: string | undefined;
  itemKind: "page" | "database" | "database_row";
  metadata: Record<string, unknown>;
}): UnifiedDocument {
  const document = normalizeDocument({
    id: input.id,
    title: input.title,
    text: input.text,
    contentState: input.contentState,
    sourceUrl: input.sourceUrl,
    modifiedAt: input.modifiedAt,
    provider: "notion",
    metadata: input.metadata
  }).document;
  return {
    ...document,
    workspaceId: input.workspaceId,
    itemKind: input.itemKind,
    ...(input.parentExternalId ? { parentExternalId: input.parentExternalId } : {}),
    ...(input.createdAt ? { createdAt: input.createdAt } : {})
  };
}

function unavailableDocument(
  id: string,
  workspaceId: string,
  parentExternalId: string | undefined,
  itemKind: "page" | "database" | "database_row",
  scopeMode: string
): UnifiedDocument {
  return typedNotionDocument({
    id,
    title: `Unavailable Notion ${itemKind} ${id}`,
    text: "",
    contentState: "extraction_failed",
    workspaceId,
    parentExternalId,
    itemKind,
    metadata: { scopeMode, selectionScoped: true }
  });
}

function canonicalScope(scope: NotionSyncMetadata["notionScope"]): CanonicalScope | null {
  if (!scope || (scope.mode !== "all_accessible" && scope.mode !== "selected_roots")) return null;
  const includedRootIds = uniqueSorted(scope.includedRootIds ?? []);
  if (scope.mode === "selected_roots" && includedRootIds.length === 0) return null;
  return {
    mode: scope.mode,
    includedRootIds,
    excludedIds: uniqueSorted(scope.excludedIds ?? []),
    includeDescendants: scope.includeDescendants ?? true,
    includeNewContent: scope.includeNewContent ?? true
  };
}

async function resolveWorkspaceId(nango: Pick<NotionSyncRuntime, "get">): Promise<string> {
  const response = (await nango.get<NotionBotResponse>({ retries: 3, endpoint: "/v1/users/me" })).data;
  const workspaceId = canonicalNotionId(response.bot?.workspace_id);
  if (!workspaceId || workspaceId.length > 512) throw new Error("notion_workspace_identity_unavailable");
  return workspaceId;
}

function resolveParentId(parent: NotionParent | undefined): string | undefined {
  if (!parent || parent.workspace) return undefined;
  const parentId = parent.page_id ?? parent.database_id ?? parent.data_source_id ?? parent.block_id;
  return parentId ? canonicalNotionId(parentId) : undefined;
}

function canonicalNotionId(value: string | undefined): string {
  return value?.trim().replace(/^notion:/, "") ?? "";
}

function uniqueSorted(values: string[]): string[] {
  return [...new Set(values.map(canonicalNotionId).filter(Boolean))]
    .sort((left, right) => left < right ? -1 : left > right ? 1 : 0);
}

function assertDatabaseObjectContract(database: NotionDatabase): void {
  if (database.object !== "database" && database.object !== "data_source") {
    throw new Error("notion_api_version_contract_mismatch");
  }
}

function notionPageTitle(page: NotionPage): string {
  for (const property of Object.values(page.properties ?? {})) {
    const value = richText(property.title);
    if (value) return value;
  }
  return richText(page.title) || canonicalNotionId(page.id) || "Untitled Notion page";
}

function notionPropertyLines(properties: Record<string, NotionProperty>): string[] {
  const lines: string[] = [];
  for (const [name, property] of Object.entries(properties)) {
    if (property.type === "title" || sensitivePropertyName.test(name)) continue;
    const value = notionPropertyValue(property);
    if (value) lines.push(`${name}: ${value}`);
  }
  return lines;
}

function notionPropertyValue(property: NotionProperty): string {
  switch (property.type) {
    case "rich_text": return richText(property.rich_text);
    case "number": return property.number === null || property.number === undefined ? "" : String(property.number);
    case "select": return property.select?.name ?? "";
    case "status": return property.status?.name ?? "";
    case "multi_select": return (property.multi_select ?? []).map((value) => value.name ?? "").filter(Boolean).join(", ");
    case "date": return dateValue(property.date);
    case "formula": return formulaValue(property.formula);
    case "rollup": return rollupValue(property.rollup);
    case "checkbox": return property.checkbox === undefined ? "" : property.checkbox ? "Yes" : "No";
    case "url": return property.url ?? "";
    case "email":
    case "phone_number": return "";
    case "people": return (property.people ?? []).map((value) => value.name ?? value.id ?? "").filter(Boolean).join(", ");
    case "relation": return (property.relation ?? []).map((value) => value.id ?? "").filter(Boolean).join(", ");
    case "files": return (property.files ?? []).map((value) => value.name ?? "").filter(Boolean).join(", ");
    case "created_time": return property.created_time ?? "";
    case "last_edited_time": return property.last_edited_time ?? "";
    case "created_by": return property.created_by?.name ?? property.created_by?.id ?? "";
    case "last_edited_by": return property.last_edited_by?.name ?? property.last_edited_by?.id ?? "";
    default: return richText(property.title) || richText(property.rich_text);
  }
}

function formulaValue(value: NotionProperty["formula"]): string {
  if (!value) return "";
  if (value.type === "string") return value.string ?? "";
  if (value.type === "number") return value.number === null || value.number === undefined ? "" : String(value.number);
  if (value.type === "boolean") return value.boolean === null || value.boolean === undefined ? "" : value.boolean ? "Yes" : "No";
  if (value.type === "date") return dateValue(value.date);
  return "";
}

function rollupValue(value: NotionProperty["rollup"]): string {
  if (!value) return "";
  if (value.type === "number") return value.number === null || value.number === undefined ? "" : String(value.number);
  if (value.type === "date") return dateValue(value.date);
  if (value.type === "array") return (value.array ?? []).map(notionPropertyValue).filter(Boolean).join(", ");
  return "";
}

function dateValue(value: { start?: string; end?: string | null } | null | undefined): string {
  if (!value?.start) return "";
  return value.end ? `${value.start} to ${value.end}` : value.start;
}

function richText(value: RichText[] | undefined): string {
  return (value ?? []).map((item) => item.plain_text ?? "").join("");
}

function blockText(block: NotionBlock): string[] {
  const value = block[block.type ?? ""];
  if (!value || typeof value !== "object") return [];
  const typed = value as { rich_text?: RichText[]; caption?: RichText[]; title?: string; expression?: string; cells?: RichText[][] };
  const lines: string[] = [];
  const main = richText(typed.rich_text);
  if (main) lines.push(main);
  const caption = richText(typed.caption);
  if (caption) lines.push(caption);
  if (typed.title) lines.push(typed.title);
  if (typed.expression) lines.push(typed.expression);
  for (const cell of typed.cells ?? []) {
    const text = richText(cell);
    if (text) lines.push(text);
  }
  return lines;
}
