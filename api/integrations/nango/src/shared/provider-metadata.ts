import { isSensitiveKey, maskSensitiveText } from "./safe-text.js";

type MetadataValue = string | number | boolean;

export function safeProviderMetadata(
  input: Record<string, unknown>,
  keys: readonly string[]
): Record<string, unknown> {
  const metadata: Record<string, unknown> = {};

  for (const key of keys) {
    if (isSensitiveKey(key) || !(key in input)) {
      continue;
    }

    const value = input[key];
    if (isMetadataValue(value)) {
      metadata[key] = typeof value === "string" ? maskSensitiveText(value) : value;
    }
  }

  return metadata;
}

export function addMetadataValue(
  metadata: Record<string, unknown>,
  key: string,
  value: unknown
): void {
  if (isSensitiveKey(key) || !isMetadataValue(value)) {
    return;
  }

  metadata[key] = typeof value === "string" ? maskSensitiveText(value) : value;
}

function isMetadataValue(value: unknown): value is MetadataValue {
  return typeof value === "string" ||
    typeof value === "number" ||
    typeof value === "boolean";
}
