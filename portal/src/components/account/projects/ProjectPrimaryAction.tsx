'use client'

import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import type { ProjectsApi } from '@/components/account/api-types'
import { DiscoveryTerminalIncidentCopy } from '@/components/account/DiscoveryJourney'
import {
  activeRunStatuses,
  resumableRunStatuses,
} from '@/components/account/helpers'
import type { AccountLabels } from '@/components/account/labels'
import type { GenerationProjectSummary, GenerationRunSummary } from '@/components/account/types'
import type { Locale } from '@/i18n/config'

export type PrimaryActionKind =
  | 'go'
  | 'agent-working'
  | 'waiting-status'
  | 'generate-with-assumptions'
  | 'open-erp'
  | 'none'

/**
 * One primary action per project card: GO before the first run, then
 * open ERP once the run has tested output. 'agent-working' and
 * 'waiting-status' still classify the run, but render nothing — the progress
 * panel narrates those states instead.
 */
export function projectPrimaryActionKind(latestRun: GenerationRunSummary | undefined): PrimaryActionKind {
  if (!latestRun) return 'go'
  if (['failed', 'cancelled'].includes(latestRun.status)) return 'none'
  if (activeRunStatuses.has(latestRun.status) && !resumableRunStatuses.has(latestRun.status)) {
    return 'agent-working'
  }
  if (resumableRunStatuses.has(latestRun.status)) {
    return latestRun.pendingWorkItems?.some((item) =>
      item.sourceStage === 'discovery_assessment' &&
      item.blocking !== false &&
      !['resolved', 'cancelled', 'superseded'].includes(item.status))
      ? 'generate-with-assumptions'
      : 'waiting-status'
  }
  if (latestRun.erp?.status === 'ready' && latestRun.erp.testsStatus === 'passed' && latestRun.erp.url) return 'open-erp'
  return 'none'
}

type ProjectPrimaryActionProps = {
  labels: AccountLabels
  locale: Locale
  project: GenerationProjectSummary
  latestRun?: GenerationRunSummary
  projectsApi: ProjectsApi
  compact?: boolean
}

function connectorBlockingCopy(labels: AccountLabels, reason?: string) {
  switch (reason) {
    case 'connector_syncing':
      return labels.connectorSyncingBeforeGeneration
    case 'connector_sync_failed':
      return labels.connectorSyncFailedBeforeGeneration
    case 'connector_reconnect_required':
      return labels.connectorReconnectBeforeGeneration
    case 'connector_expired':
      return labels.connectorExpiredBeforeGeneration
    default:
      return labels.connectorSetupBeforeGeneration
  }
}

export function ProjectPrimaryAction({
  labels,
  locale,
  project,
  latestRun,
  projectsApi,
  compact = false,
}: ProjectPrimaryActionProps) {
  const {
    generationStarting,
    pendingAction,
    startGeneration,
    generateWithAssumptions,
  } = projectsApi

  const kind = projectPrimaryActionKind(latestRun)
  const canStartGeneration = !latestRun
  const connectorSetupPending = canStartGeneration && Boolean(project.generationBlockedReason)
  const generationQueuedForEvidence = canStartGeneration && project.status === 'waiting_for_evidence'
  const terminalInternalIncident = Boolean(latestRun && ['failed', 'cancelled'].includes(latestRun.status))
  const starting = generationStarting === project.id

  // While the agent is working, or while we wait on a person, this box had
  // nothing to offer but a disabled button and a sentence that "Where things
  // stand" already says better. Show it only when there is something to do.
  const hasAction = kind === 'go' ||
    kind === 'open-erp' ||
    kind === 'generate-with-assumptions'
  if ((!hasAction && !terminalInternalIncident) || (compact && !hasAction)) return null

  return (
    <div
      className="flex flex-col gap-2"
      aria-label={labels.primaryAction}
    >
      {!compact && terminalInternalIncident ? (
        <DiscoveryTerminalIncidentCopy locale={locale} />
      ) : null}

      {kind === 'go' ? (
        <div className="flex flex-col gap-2">
          {!compact && generationQueuedForEvidence ? (
            <p className="m-0 text-sm text-muted-foreground">
              {locale === 'bg' ? 'Заявката е запазена. Генерирането ще започне автоматично след синхронизацията.' : 'Your request is saved. Generation will start automatically as soon as synchronization finishes.'}
            </p>
          ) : !compact && connectorSetupPending ? (
            <p className="m-0 text-sm text-muted-foreground">
              {connectorBlockingCopy(labels, project.generationBlockedReason)}
            </p>
          ) : null}
          <Button
            type="button"
            size={compact ? 'sm' : 'default'}
            onClick={() => startGeneration(project.id)}
            disabled={starting || connectorSetupPending || generationQueuedForEvidence}
          >
            {starting ? <Spinner data-icon="inline-start" /> : null}
            {starting
              ? labels.startingGeneration
              : generationQueuedForEvidence
                ? (locale === 'bg' ? 'Очакваме фирмените данни' : 'Waiting for company data')
                : labels.startGeneration}
          </Button>
        </div>
      ) : null}

      {kind === 'generate-with-assumptions' ? (
        <div className="flex flex-col gap-2">
          {!compact && <p className="text-sm text-muted-foreground">{labels.generateWithAssumptionsHelper}</p>}
          <div>
            <Button
              type="button"
              variant="outline"
              onClick={() => generateWithAssumptions(project.id)}
              disabled={pendingAction === `generate-with-assumptions:${project.id}`}
            >
              {pendingAction === `generate-with-assumptions:${project.id}` ? <Spinner data-icon="inline-start" /> : null}
              {pendingAction === `generate-with-assumptions:${project.id}`
                ? labels.generatingWithAssumptions
                : labels.generateWithAssumptions}
            </Button>
          </div>
        </div>
      ) : null}

      {kind === 'open-erp' && latestRun ? <form method="post" action={`/api/customers/generation-runs/${latestRun.id}/open`}><Button size={compact ? 'sm' : 'default'} type="submit">{locale === 'bg' ? 'Отвори ERP' : 'Open ERP'}</Button></form> : null}

    </div>
  )
}
