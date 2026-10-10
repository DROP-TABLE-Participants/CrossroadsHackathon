export type UnifiedGenericRecord = {
  id: string;
  title: string;
  text: string;
  kind: "generic";
  provider: string;
  metadata?: Record<string, unknown>;
};
