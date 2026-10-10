import { createSync } from "nango";
import * as z from "zod";
import { UnifiedDocument } from "../../src/models/unified-document.js";
import {
  streamNotionDocuments,
  type NotionSyncCheckpoint
} from "../../src/files/notion/list-documents.js";

export default createSync({
  description: "Synchronizes all Notion content allowed by the customer's saved scope rule.",
  version: "3.0.1",
  frequency: "every hour",
  autoStart: false,
  models: { UnifiedDocument },
  metadata: z.object({
    notionScope: z.object({
      mode: z.enum(["all_accessible", "selected_roots"]),
      includedRootIds: z.array(z.string()).default([]),
      excludedIds: z.array(z.string()).default([]),
      includeDescendants: z.boolean().default(true),
      includeNewContent: z.boolean().default(true)
    }).optional()
  }),
  checkpoint: z.object({
    scopeKey: z.string(),
    searchCursor: z.string(),
    rootIndex: z.number(),
    processed: z.number(),
    failed: z.number()
  }),
  exec: async (nango) => {
    const checkpoint = await nango.getCheckpoint() as NotionSyncCheckpoint | null;
    if (!checkpoint) await nango.trackDeletesStart("UnifiedDocument");
    await streamNotionDocuments(nango, {
      checkpoint,
      onBatch: async (documents) => {
        await nango.batchSave(documents, "UnifiedDocument");
      },
      onCheckpoint: async (next) => {
        await nango.saveCheckpoint(next);
      }
    });
    await nango.trackDeletesEnd("UnifiedDocument");
    await nango.clearCheckpoint();
  }
});
