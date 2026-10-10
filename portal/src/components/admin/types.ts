export type AdminCustomer = {
  id: string
  email: string
  name: string
  companyName?: string
  isActive: boolean
  createdAt: string
  updatedAt: string
  generationProjectCount: number
}

export type AdminProject = {
  id: string
  customerId: string
  companyName: string
  customerName: string
  customerEmail: string
  status: string
  discoveryMode: string
  latestRunStatus?: string
  pendingWorkCount: number
  updatedAt: string
}

export type AdminAuditRecord = {
  id: string
  actorAdminAccountId: string
  actorEmail: string
  action: string
  resourceType: string
  resourceId: string
  safeDetailsJson: string
  createdAt: string
}

export type AdminOverview = {
  activeCustomerCount: number
  inactiveCustomerCount: number
  generationProjectCount: number
  needsAttentionProjectCount: number
  failedProjectCount: number
  pendingNotificationCount: number
  failedNotificationCount: number
  activeAdministratorCount: number
  recentProjects: AdminProject[]
  recentAuditRecords: AdminAuditRecord[]
}

export type AdminCustomerDetails = {
  customer: AdminCustomer
  projects: AdminProject[]
  recentAuditRecords: AdminAuditRecord[]
}

export type AdminPendingWork = {
  id: string
  kind: string
  status: string
  blocking: boolean
  prompt: string
  targetRole?: string
  createdAt: string
}

export type AdminInputSource = {
  id: string
  providerConfigKey: string
  sourceKind: string
  status: string
  scopeMode: string
  recordCount: number
  textRecordCount: number
  lastError?: string
  lastWarning?: string
  lastCheckedAt?: string
  lastSyncedAt?: string
}

export type AdminDeployment = {
  id: string
  environment: string
  status: string
  runtimeUrl?: string
  testsStatus?: string
  attemptCount: number
  failureCode?: string
  failureMessage?: string
  updatedAt: string
}

export type AdminProjectDetails = {
  id: string
  customerId: string
  companyName: string
  customerName: string
  customerEmail: string
  status: string
  discoveryMode: string
  goal: string
  industry?: string
  companyType?: string
  inputSourceCount: number
  evidenceDocumentCount: number
  interviewCount: number
  answeredInterviewCount: number
  latestRunId?: string
  latestRunStatus?: string
  latestRunClaimCount: number
  failureCode?: string
  failureMessage?: string
  heartbeatAt?: string
  evidenceSnapshotSequence?: number
  evidenceSnapshotHash?: string
  evidenceSnapshotItemCount: number
  pendingWork: AdminPendingWork[]
  inputSources: AdminInputSource[]
  deployments: AdminDeployment[]
  recentAuditRecords: AdminAuditRecord[]
}

export type AdminGenerationRun = {
  id: string
  generationProjectId: string
  customerId: string
  companyName: string
  customerEmail: string
  status: string
  requestedMode: string
  actualMode?: string
  currentStage?: string
  claimCount: number
  failureCount: number
  failureDisposition?: string
  failureCode?: string
  nextAttemptAt?: string
  heartbeatAt?: string
  createdAt: string
  updatedAt: string
}

export type AdminOrchestrationOverview = {
  queuedRunCount: number
  runningRunCount: number
  resumingRunCount: number
  resumeFailedRunCount: number
  waitingForInputRunCount: number
  cancellationRequestedRunCount: number
  retryScheduledRunCount: number
  failedLast24HoursCount: number
  staleLeaseCount: number
  oldestQueuedAt?: string
  recentRuns: AdminGenerationRun[]
}

export type AdminGenerationStage = {
  id: string
  stageKey: string
  status: string
  sequence: number
  attemptCount: number
  inputHash?: string
  summary?: string
  failureCode?: string
  failureMessage?: string
  startedAt: string
  completedAt?: string
}

export type AdminGenerationRunEvent = {
  id: string
  sequence: number
  eventType: string
  stageKey?: string
  attemptNumber?: number
  summary: string
  safeMetadataJson: string
  createdAt: string
}

export type AdminGenerationModelCall = {
  id: string
  workItemId?: string
  attemptId?: string
  phase: string
  status: string
  requestHash: string
  providerResponseId?: string
  agentKey?: string
  agentVersion?: string
  provider?: string
  model?: string
  promptVersion?: string
  contractVersion?: string
  durationMilliseconds?: number
  inputTokenCount?: number
  outputTokenCount?: number
  responseHash?: string
  failureCode?: string
  createdAt: string
  updatedAt: string
}

export type AdminAgentExecutionAttempt = {
  id: string
  attemptNumber: number
  status: string
  agentKey: string
  agentVersion: string
  definitionHash: string
  inputManifestHash: string
  outputArtifactHash?: string
  failureDisposition?: string
  failureCode?: string
  failureMessage?: string
  repairInstructions?: string
  modelCallCount: number
  startedAt: string
  completedAt?: string
}

export type AdminAgentWorkItem = {
  id: string
  stageId: string
  parentWorkItemId?: string
  workItemKey: string
  agentKey: string
  agentVersion: string
  agentRole: string
  agentLayer: string
  contractVersion: string
  definitionHash: string
  supervisorAgentKey?: string
  status: string
  attemptCount: number
  repairCount: number
  routingDepth: number
  inputManifestHash: string
  summary?: string
  failureDisposition?: string
  failureCode?: string
  failureMessage?: string
  leaseExpiresAt?: string
  heartbeatAt?: string
  startedAt?: string
  completedAt?: string
  attempts: AdminAgentExecutionAttempt[]
}

export type AdminGenerationArtifact = {
  id: string
  workItemId: string
  attemptId: string
  supersedesArtifactVersionId?: string
  artifactKey: string
  artifactType: string
  schemaName: string
  schemaVersion: string
  version: number
  contentHash: string
  inputManifestHash: string
  evidenceSnapshotHash: string
  producerAgentKey: string
  producerAgentVersion: string
  producerDefinitionHash: string
  validationStatus: string
  validationSummary?: string
  createdAt: string
}

export type AdminGenerationRunDetails = {
  id: string
  generationProjectId: string
  customerId: string
  companyName: string
  customerEmail: string
  status: string
  requestedMode: string
  actualMode?: string
  claimCount: number
  failureCount: number
  failureDisposition?: string
  failureCode?: string
  failureMessage?: string
  nextAttemptAt?: string
  leaseOwner?: string
  leaseExpiresAt?: string
  heartbeatAt?: string
  createdAt: string
  updatedAt: string
  startedAt?: string
  completedAt?: string
  cancellationRequestedAt?: string
  cancelledAt?: string
  evidenceSnapshotSequence?: number
  evidenceSnapshotHash?: string
  evidenceSnapshotItemCount: number
  canRetryNow: boolean
  canCancel: boolean
  canResume: boolean
  stages: AdminGenerationStage[]
  agentWorkItems: AdminAgentWorkItem[]
  artifacts: AdminGenerationArtifact[]
  events: AdminGenerationRunEvent[]
  modelCalls: AdminGenerationModelCall[]
  pendingWork: AdminPendingWork[]
  deployments: AdminDeployment[]
}

export type AdminAdministrator = {
  id: string
  email: string
  name: string
  isActive: boolean
  role: string
  createdAt: string
  updatedAt: string
}

export type AdminNotification = {
  id: string
  kind: string
  recipient: string
  status: string
  attemptCount: number
  sentAt?: string
  nextAttemptAt?: string
  lastError?: string
}

export type AdminDemoBooking = {
  id: string
  customerId: string
  email: string
  representativeName: string
  companyName: string
  startDate: string
  endDate: string
  status: string
  notifications: AdminNotification[]
}

export type AdminPaged<T> = {
  page: number
  pageSize: number
  totalCount: number
} & T
