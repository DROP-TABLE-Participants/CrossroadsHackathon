import * as z from "zod";

export const UnifiedMessage = z.object({
  id: z.string(),
  channelOrThread: z.string(),
  author: z.string().optional(),
  text: z.string(),
  timestamp: z.string().optional(),
  provider: z.string(),
  metadata: z.record(z.string(), z.unknown()).optional()
});

export type UnifiedMessage = z.infer<typeof UnifiedMessage>;
