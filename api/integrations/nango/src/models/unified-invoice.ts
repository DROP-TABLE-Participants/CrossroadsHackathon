export type UnifiedInvoice = {
  id: string;
  customerName?: string;
  invoiceNumber?: string;
  amount?: number;
  status?: string;
  dueDate?: string;
  provider: string;
  metadata?: Record<string, unknown>;
};
