export type UnifiedIssue = {
  id: string;
  title: string;
  description?: string;
  status?: string;
  assignee?: string;
  updatedAt?: string;
  provider: string;
  metadata?: Record<string, unknown>;
};
