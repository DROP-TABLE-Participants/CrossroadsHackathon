import * as z from "zod";

export const UnifiedDocumentContentState = z.enum([
  "content_extracted",
  "metadata_only",
  "unsupported",
  "extraction_failed"
]);

export const UnifiedDocument = z.object({
  id: z.string(),
  title: z.string(),
  text: z.string(),
  contentState: UnifiedDocumentContentState,
  sourceUrl: z.string().optional(),
  provider: z.string(),
  mimeType: z.string().optional(),
  modifiedAt: z.string().optional(),
  createdAt: z.string().optional(),
  workspaceId: z.string().optional(),
  parentExternalId: z.string().optional(),
  itemKind: z.enum(["page", "database", "database_row"]).optional(),
  author: z.string().optional(),
  metadata: z.record(z.string(), z.unknown()).optional()
});

export type UnifiedDocument = z.infer<typeof UnifiedDocument>;
export type UnifiedDocumentContentState = z.infer<typeof UnifiedDocumentContentState>;

export type UnifiedDocumentShape = {
  id: string;
  title: string;
  text: string;
  contentState: UnifiedDocumentContentState;
  sourceUrl?: string;
  provider: string;
  mimeType?: string;
  modifiedAt?: string;
  createdAt?: string;
  workspaceId?: string;
  parentExternalId?: string;
  itemKind?: "page" | "database" | "database_row";
  author?: string;
  metadata?: Record<string, unknown>;
};
