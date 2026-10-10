import type { UnifiedMessage } from "../models/unified-message.js";
import { safeProviderMetadata } from "../shared/provider-metadata.js";
import { normalizeText } from "../shared/safe-text.js";

export type MessageNormalizationInput = {
  id?: string | undefined;
  channelOrThread?: string | undefined;
  author?: string | undefined;
  text?: string | undefined;
  timestamp?: string | undefined;
  provider: string;
  metadata?: Record<string, unknown>;
  metadataKeys?: readonly string[];
};

export function normalizeMessage(input: MessageNormalizationInput): UnifiedMessage | null {
  const id = normalizeText(input.id, 300);
  const text = normalizeText(input.text);
  if (id.length === 0 || text.length === 0) {
    return null;
  }

  const message: UnifiedMessage = {
    id,
    channelOrThread: normalizeText(input.channelOrThread, 500) || "unknown",
    text,
    provider: input.provider
  };
  assignIfPresent(message, "author", normalizeText(input.author, 300));
  assignIfPresent(message, "timestamp", normalizeText(input.timestamp, 100));

  const metadata = safeProviderMetadata(input.metadata ?? {}, input.metadataKeys ?? []);
  if (Object.keys(metadata).length > 0) {
    message.metadata = metadata;
  }

  return message;
}

function assignIfPresent<T extends object, K extends keyof T>(target: T, key: K, value: T[K] | undefined): void {
  if (value !== undefined && value !== "") {
    target[key] = value;
  }
}
