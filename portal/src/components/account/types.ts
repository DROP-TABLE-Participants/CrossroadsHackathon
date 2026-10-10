export type TenantSummary = {
  id: string
  stableId: string
  slug: string
  companyName: string
  plan: string
  status: string
  setupStatus: string
  environment?: string
  host?: string
  runtimeUrl?: string
  deploymentStatus?: string
  latestStagedBundleVersion?: string
  deploymentHistory?: Array<{
    environment: string
    status: string
    bundleVersion?: string
    updatedAt?: string
  }>
}

export type ErpFeedbackSummary = {
  id: string
  tenantId?: string
  tenantEnvironmentId?: string
  sourceEnvironment?: string
  runtimeUserEmail?: string
  entityKey?: string
  recordId?: string
  pagePath?: string
  category?: string
  message: string
  status: string
  createdAt?: string
}

export type ErpUsageObservationSummary = {
  id: string
  tenantId?: string
  tenantEnvironmentId?: string
  sourceEnvironment?: string
  status: string
  kind: string
  severity: string
  title: string
  summary: string
  recommendation?: {
    recommendationType?: string
    summary?: string
    proposalType?: string
    supportedByCurrentRuntime?: boolean
  }
  evidenceCount?: number
  relatedEntityKey?: string
  relatedReportKey?: string
  relatedWorkflowKey?: string
  proposalId?: string
  createdAt?: string
}

export type ErpChangeProposalSummary = {
  id: string
  tenantId?: string
  tenantEnvironmentId?: string
  feedbackId?: string
  usageObservationId?: string
  status: string
  proposalType: string
  title: string
  summary: string
  risk: string
  supportedByCurrentRuntime: boolean
  proposedChanges?: Array<Record<string, unknown>>
  unsupportedRuntimeNeeds?: Array<{
    needType?: string
    summary?: string
    reason?: string
  }>
  createdBy?: string
  createdAt?: string
}

export type ErpChangeJobSummary = {
  id: string
  proposalId?: string
  tenantId?: string
  environmentId?: string
  status: string
  stageStatus?: string
  baseBundleVersion?: string
  baseBundleHash?: string
  patchedBundleVersion?: string
  patchedBundleHash?: string
  compileStatus?: string
  safeError?: string
  generationProjectId?: string
  patchSummary?: string
  patchOperations?: Array<{
    operationType?: string
    entityKey?: string
    fieldKey?: string
    reportKey?: string
    widgetKey?: string
    label?: string
  }>
  completedAt?: string
  createdAt?: string
}

export type GenerationProjectSummary = {
  id: string
  tenantId?: string
  companyName: string
  industry?: string
  companyType?: string
  goal?: string
  discoveryMode?: string
  status?: string
  ownerName?: string
  ownerEmail?: string
  participants?: Array<{
    id: string
    name: string
    email?: string
    roleTitle?: string
    department?: string
    level?: string
    status?: string
  }>
  requests?: Array<{
    id: string
    participantId?: string
    status?: string
    questionSetKey?: string
    expiresAt?: string
    sentAt?: string
    inviteLink?: string
    completedAt?: string
    inviteDelivery?: InviteDeliverySummary
  }>
  generationRuns?: GenerationRunSummary[]
  inputSourceCount?: number
  inputSources?: GenerationInputSourceSummary[]
  availableConnectors?: string[]
  canStartGeneration?: boolean
  generationBlockedReason?: string
  /** Creation of the first project starts the customer's free trial. */
  createdAt?: string
}

export type InviteDeliverySummary = {
  status: 'not_sent' | 'queued' | 'sent' | 'failed' | 'skipped_no_email'
  recipientEmail?: string
  sentAt?: string
  lastAttemptAt?: string
  failureReasonSafe?: string
  provider?: string
  attemptCount: number
}

export type GenerationInputSourceSummary = {
  id: string
  provider?: string
  displayName?: string
  status?: string
  lastSyncedAt?: string
  lastCheckedAt?: string
  lastSyncRequestedAt?: string
  lastRecordCount?: number | null
  lastTextRecordCount?: number | null
  lastError?: string
  lastWarning?: string
  lastConnectionStatus?: string
  selectedResourceCount?: number
  requiresSelection?: boolean
  retainContentUntil?: string
  scopeMode?: 'unconfigured' | 'all_accessible' | 'selected_roots'
  excludedResourceCount?: number
  includeDescendants?: boolean
  includeNewContent?: boolean
  syncDiscoveredCount?: number | null
  syncProcessedCount?: number
  syncFailedCount?: number
  includedRootIds?: string[]
  excludedIds?: string[]
  generationBlockReason?: string
}

export type ConnectorResourceSummary = {
  id: string
  title: string
  kind: string
  sourceUrl?: string
  modifiedAt?: string
  selected: boolean
  parentId?: string
  sortIndex?: number
}

export type ConnectorScopeInput = {
  mode: 'all_accessible' | 'selected_roots'
  includedRootIds: string[]
  excludedIds: string[]
  includeDescendants: boolean
  includeNewContent: boolean
}

export type ConnectorOption = {
  key: string
  title: string
  description: string
  status: 'available' | 'coming_soon' | 'not_configured' | 'connecting' | 'connected' | 'failed'
}

export type GenerationRunSummary = {
  id: string
  status: string
  mode?: string
  requestedMode?: string
  actualMode?: string
  generationModeLabel?: string
  fallbackUsed?: boolean
  resumedFromRunId?: string
  attemptNumber?: number
  resumeReason?: string
  summary?: string
  lastError?: string
  lastEventAt?: string
  heartbeatAt?: string
  isStale?: boolean
  startedAt?: string
  completedAt?: string
  failedAt?: string
  erp?: {
    status: string
    url?: string
    deploymentId?: string
    bundleVersion?: string
    bundleHash?: string
    readyAt?: string
    lastError?: string
    testsStatus?: string
    activation?: RuntimeActivationSummary
  }
  generatedErp?: {
    entities: string[]
    workflows: string[]
    actions: string[]
    reports: string[]
    dashboards: string[]
    permissions: string[]
    openIssues: string[]
  }
  stages: Array<{
    id: string
    stage: string
    status: string
    summary?: string
  }>
  artifacts: Array<{
    id: string
    kind: string
    status: string
    summary?: string
    label: string
  }>
  pendingWorkItems: Array<{
    id: string
    kind: string
    status: string
    blocking?: boolean
    targetRole?: string
    question?: string
    reason?: string
    sourceStage?: string
    linkedInterviewRequestId?: string
    linkedInputDocumentId?: string
    resolutionNote?: string
    metadata?: Record<string, string>
    createdAt?: string
    resolvedAt?: string
  }>
}

export type RuntimeActivationSummary = {
  status: 'not_created' | 'pending' | 'activated' | 'expired' | 'revoked'
  activationUrlAvailable: boolean
  activationCreatedAt?: string
  activationExpiresAt?: string
  activatedAt?: string
  activationLastGeneratedAt?: string
  activationEnvironment: 'erp'
  activationEmail?: string
}

export type OneTimeActivationLink = {
  environment: 'erp'
  status: string
  activationUrl: string
  activationEmail?: string
  activationExpiresAt?: string
}

export type GeneratedBundleOption = {
  id: string
  runId: string
  projectId: string
  label: string
  entityCount: number
  workflowCount: number
  reportCount: number
  status: string
}
