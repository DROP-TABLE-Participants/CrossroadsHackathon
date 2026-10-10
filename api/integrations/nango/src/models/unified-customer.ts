export type UnifiedCustomer = {
  id: string;
  name: string;
  email?: string;
  phone?: string;
  sourceUrl?: string;
  provider: string;
  metadata?: Record<string, unknown>;
};
