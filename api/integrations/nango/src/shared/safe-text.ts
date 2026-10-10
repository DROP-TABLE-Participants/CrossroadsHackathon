const BEARER_PATTERN = /Bearer\s+[A-Za-z0-9._~+/=-]+/gi;
const SECRET_ASSIGNMENT_PATTERN = /((?:api[_-]?key|access[_-]?token|refresh[_-]?token|secret|token|password|authorization|credential)\s*[=:]\s*)\S+/gi;

export function isSensitiveKey(key: string): boolean {
  const normalized = key.replace(/[-_\s]/g, "").toLowerCase();
  return normalized.includes("token") ||
    normalized.includes("secret") ||
    normalized.includes("password") ||
    normalized.includes("apikey") ||
    normalized.includes("authorization") ||
    normalized.includes("credential");
}

export function maskSensitiveText(value: string): string {
  return value
    .replace(BEARER_PATTERN, "Bearer ***masked***")
    .replace(SECRET_ASSIGNMENT_PATTERN, "***masked***");
}

export function normalizeText(value: unknown, maxCharacters = Number.POSITIVE_INFINITY): string {
  if (value === null || value === undefined) {
    return "";
  }

  const raw = typeof value === "string" ? value : String(value);
  const normalized = maskSensitiveText(raw)
    .replace(/\r\n?/g, "\n")
    .replace(/[ \t]+\n/g, "\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim();

  return normalized.length > maxCharacters ? normalized.slice(0, maxCharacters).trimEnd() : normalized;
}

export function normalizeTitle(value: unknown, fallback: string): string {
  const title = normalizeText(value, 300);
  return title.length > 0 ? title : fallback;
}
