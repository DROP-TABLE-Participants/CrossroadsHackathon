'use client'

import { XCircle } from '@phosphor-icons/react'

import { Button } from '@/components/ui/button'
import type { FeedbackApi } from '@/components/account/api-types'
import type { AccountLabels } from '@/components/account/labels'
import type {
  ErpChangeProposalSummary,
  ErpUsageObservationSummary,
} from '@/components/account/types'

type ObservationCardProps = {
  labels: AccountLabels
  observation: ErpUsageObservationSummary
  tenantName: string
  proposal?: ErpChangeProposalSummary
  feedbackApi: FeedbackApi
}

/**
 * One usage observation surfaced by the durable analysis agent. Read-only
 * except for a Dismiss control while no proposal has been created yet.
 */
export function ObservationCard({
  labels,
  observation,
  tenantName,
  proposal,
  feedbackApi,
}: ObservationCardProps) {
  const { erpFeedbackAction, dismissUsageObservation } = feedbackApi
  const dismissBusy = erpFeedbackAction === `observation-dismiss:${observation.id}`

  const relatedMeta = [
    observation.evidenceCount ? `${observation.evidenceCount} evidence` : '',
    observation.relatedEntityKey,
    observation.relatedReportKey,
    observation.relatedWorkflowKey,
  ].filter(Boolean).join(' / ')

  return (
    <article className="flex flex-col gap-1.5 rounded-lg border bg-muted/20 p-3">
      <div className="flex flex-col">
        <strong className="text-sm font-semibold">{observation.title}</strong>
        <span className="text-xs text-muted-foreground">
          {tenantName || observation.sourceEnvironment || 'ERP'} / {observation.kind.replace(/_/g, ' ')} / {observation.severity}
        </span>
      </div>
      <p className="text-sm">{observation.summary}</p>
      {observation.recommendation?.summary ? (
        <p className="text-sm text-muted-foreground">{observation.recommendation.summary}</p>
      ) : null}
      {relatedMeta ? <span className="text-xs text-muted-foreground">{relatedMeta}</span> : null}
      {proposal ? (
        <span className="text-xs text-muted-foreground">{proposal.title}</span>
      ) : observation.status !== 'proposal_created' ? (
        <div>
          <Button
            type="button"
            size="sm"
            variant="ghost"
            onClick={() => dismissUsageObservation(observation.id)}
            disabled={dismissBusy}
          >
            <XCircle data-icon="inline-start" />
            {labels.dismissObservation}
          </Button>
        </div>
      ) : null}
    </article>
  )
}
