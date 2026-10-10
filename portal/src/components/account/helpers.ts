import type { AccountLabels } from '@/components/account/labels'
import type {
  ConnectorOption,
  GenerationInputSourceSummary,
  GenerationProjectSummary,
  GenerationRunSummary,
  InviteDeliverySummary,
} from '@/components/account/types'
import type { Locale } from '@/i18n/config'

/**
 * A date a person can read: "Jul 16, 2026". Raw ISO timestamps
 * ("2026-07-16T15:39:57.645Z") are machine output and were leaking straight
 * onto the customer's screen. The clock time is noise here — nobody acts on the
 * millisecond a source was last checked.
 */
export function formatDate(locale: Locale, value?: string) {
  if (!value) return ''
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return ''

  return new Intl.DateTimeFormat(locale === 'bg' ? 'bg-BG' : 'en-US', {
    dateStyle: 'medium',
  }).format(date)
}

export function formatDateRange(locale: Locale, startDate?: string, endDate?: string) {
  if (!startDate || !endDate) return null

  const formatter = new Intl.DateTimeFormat(locale === 'bg' ? 'bg-BG' : 'en-US', {
    dateStyle: 'medium',
  })

  return `${formatter.format(new Date(startDate))} - ${formatter.format(new Date(endDate))}`
}

export function connectorOptions(labels: AccountLabels, availableConnectors: string[], query: string): ConnectorOption[] {
  const normalizedQuery = query.trim().toLowerCase()
  const options: ConnectorOption[] = [{ key: 'notion', title: labels.notion, description: labels.documentsAndSharedDriveFiles, status: availableConnectors.includes('notion') ? 'available' : 'not_configured' }]

  if (!normalizedQuery) return options

  return options.filter((option) =>
    `${option.title} ${option.description}`.toLowerCase().includes(normalizedQuery))
}

export function connectorStatusLabel(labels: AccountLabels, status: ConnectorOption['status']) {
  switch (status) {
    case 'available':
      return labels.available
    case 'not_configured':
      return labels.notConfigured
    case 'connecting':
      return labels.connectingDataSource
    case 'connected':
      return labels.connectorConnected
    case 'failed':
      return labels.connectorNeedsReconnect
    default:
      return labels.comingSoon
  }
}

export function sourceProviderLabel(labels: AccountLabels, provider?: string) {
  switch (provider) {
    case 'google_drive':
    case 'google-drive':
      return labels.googleDrive
    case 'sharepoint':
    case 'sharepoint-online':
      return labels.sharePoint
    case 'microsoft_teams':
    case 'microsoft-teams':
      return labels.microsoftTeams
    case 'notion':
      return labels.notion
    case 'slack':
      return labels.slack
    case 'onedrive':
      return 'OneDrive'
    default:
      return 'Documents'
  }
}

export function sourceHealthLabel(labels: AccountLabels, source: GenerationInputSourceSummary) {
  if (source.lastError) return source.lastError
  if (source.lastWarning) return source.lastWarning
  if (source.status === 'no_records') return labels.connectedButNoDocuments
  if (source.status === 'records_without_text') return labels.documentsFoundNoReadableText
  if (source.status === 'connecting') return labels.connectingDataSource
  if (source.status === 'awaiting_scope' || source.status === 'awaiting_selection') return labels.selectConnectorContent
  if (source.status === 'syncing') return labels.syncingSource
  return source.lastConnectionStatus?.replace(/_/g, ' ') || source.status?.replace(/_/g, ' ') || 'connected'
}

export function connectorSources(project: GenerationProjectSummary, connector: ConnectorOption) {
  if (connector.key === 'google-drive') {
    return project.inputSources?.filter((source) =>
      ['google_drive', 'google-drive'].includes(source.provider || ''))
  }

  if (connector.key === 'sharepoint') {
    return project.inputSources?.filter((source) =>
      ['sharepoint', 'sharepoint-online'].includes(source.provider || ''))
  }

  if (connector.key === 'microsoft-teams') {
    return project.inputSources?.filter((source) =>
      ['microsoft_teams', 'microsoft-teams'].includes(source.provider || ''))
  }

  if (connector.key === 'notion') {
    return project.inputSources?.filter((source) => source.provider === 'notion')
  }

  if (connector.key === 'slack') {
    return project.inputSources?.filter((source) => source.provider === 'slack')
  }

  return []
}

export function connectorWithSourceState(project: GenerationProjectSummary, connector: ConnectorOption): ConnectorOption {
  const sources = connectorSources(project, connector) || []
  if (!sources.length) return connector

  if (sources.some((source) =>
    ['connected', 'syncing', 'sync_failed', 'ready', 'no_records', 'records_without_text']
      .includes(source.status || ''))) {
    return { ...connector, status: 'connected' }
  }

  if (sources.some((source) =>
    source.status === 'connecting' ||
    source.status === 'draft' ||
    source.lastConnectionStatus === 'connect_session_created')) {
    return { ...connector, status: 'connecting' }
  }

  if (sources.some((source) =>
    ['requires_reconnect', 'connection_failed', 'error'].includes(source.status || ''))) {
    return { ...connector, status: 'failed' }
  }

  return connector
}

export function slugifyTenant(value: string) {
  return value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9-]+/g, '-')
    .replace(/-{2,}/g, '-')
    .replace(/^-|-$/g, '')
    .slice(0, 48)
}

export const resolvedPendingStatuses = new Set(['resolved', 'cancelled', 'superseded'])
export const resumableRunStatuses = new Set(['waiting_for_input'])
export function shouldShowRuntimeEnvironmentCreateForm(_generatedBundleCount: number) {
  return false
}

export function resolveGeneratedBundleSelection(
  currentSelection: string,
  generatedBundles: Array<{ id: string }>,
) {
  if (generatedBundles.some((bundle) => bundle.id === currentSelection)) {
    return currentSelection
  }

  return generatedBundles[0]?.id || ''
}

export function shouldShowCustomerArtifact(artifact: { kind?: string; label?: string }) {
  const kind = (artifact.kind || '').toLowerCase()
  const label = (artifact.label || '').toLowerCase()

  if (kind.includes('company_map') || label.includes('company map')) {
    return false
  }

  if (kind.includes('artifact_json') || label.includes('raw artifact')) {
    return false
  }

  return true
}

export function hasUnresolvedBlockingPendingWork(run?: GenerationRunSummary) {
  return Boolean(run?.pendingWorkItems?.some((item) => item.blocking !== false && !resolvedPendingStatuses.has(item.status)))
}

export function discoveryJourneyStep(project: GenerationProjectSummary) {
  const latestRun = project.generationRuns?.[0]

  if (latestRun?.erp?.status === 'ready' || latestRun?.generatedErp) {
    return 3
  }

  if (latestRun && !resumableRunStatuses.has(latestRun.status)) return 2
  if ((project.inputSourceCount || project.inputSources?.length || 0) > 0 || latestRun) return 1
  return 0
}

export function prioritizeCustomerPendingWorkItems(items: GenerationRunSummary['pendingWorkItems']) {
  return items
    .map((item, index) => ({ item, index }))
    .sort((left, right) => {
      const priority = (item: GenerationRunSummary['pendingWorkItems'][number]) => {
        if (!resolvedPendingStatuses.has(item.status) && item.blocking !== false) return 0
        if (!resolvedPendingStatuses.has(item.status)) return 1
        return 2
      }

      const createdAt = (item: GenerationRunSummary['pendingWorkItems'][number]) => {
        const parsed = item.createdAt ? Date.parse(item.createdAt) : Number.NaN
        return Number.isFinite(parsed) ? parsed : 0
      }

      return priority(left.item) - priority(right.item) ||
        createdAt(right.item) - createdAt(left.item) ||
        left.index - right.index
    })
    .map(({ item }) => item)
}

export function pendingKindLabel(kind: string) {
  switch (kind) {
    case 'interview_request':
      return 'Interview request'
    case 'evidence_collection_request':
      return 'More information needed'
    case 'contradiction_resolution_request':
      return 'Clarification needed'
    case 'artifact_repair_request':
      return 'Support needed'
    case 'human_escalation':
      return 'Support needed'
    default:
      return kind.replace(/_/g, ' ')
  }
}

/** A source waiting on an OAuth outcome we may never be told about. */
const awaitingConnectionStatuses = new Set(['connecting', 'draft', 'syncing'])

/**
 * Projects whose source should be checked once when a local developer returns
 * from Nango. Shared environments are updated by signed webhooks.
 */
export function awaitingConnectionProjectIds(projects: GenerationProjectSummary[]) {
  return projects
    .filter((project) => project.inputSources?.some((source) =>
      awaitingConnectionStatuses.has(source.status || 'draft')))
    .map((project) => project.id)
}

export function humanRoleLabel(value?: string) {
  const raw = (value || '').trim()
  if (!raw) return ''
  if (/[A-Z]/.test(raw) && raw.includes(' ')) return raw
  return raw
    .replace(/[_-]+/g, ' ')
    .split(' ')
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1).toLowerCase())
    .join(' ')
}

export function inviteDeliveryLabel(labels: AccountLabels, delivery?: InviteDeliverySummary) {
  switch (delivery?.status) {
    case 'sent':
      return labels.inviteSent
    case 'failed':
      return labels.inviteFailed
    case 'skipped_no_email':
      return labels.inviteNoEmail
    case 'queued':
      return labels.inviteQueued
    case 'not_sent':
    default:
      return labels.inviteNotSent
  }
}

export function inviteDeliveryFeedback(labels: AccountLabels, delivery?: InviteDeliverySummary) {
  switch (delivery?.status) {
    case 'sent':
      return 'Interview invite sent. The copy link remains available in the interview details.'
    case 'queued':
      return 'Email delivery is being retried automatically. No action is needed.'
    case 'failed':
      return 'Automatic email delivery stopped after repeated failures. The copy link remains available.'
    case 'skipped_no_email':
      return `${labels.noEmailKnown} Copy the invite link or regenerate it later.`
    default:
      return 'Interview invite link created.'
  }
}
export const activeRunStatuses = new Set([
  'queued',
  'running',
  'resuming',
  'resume_failed',
  'waiting_for_input',
  'cancellation_requested',
])
