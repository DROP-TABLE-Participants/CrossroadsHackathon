'use client'

import { Button } from '@/components/ui/button'
import type { FeedbackApi } from '@/components/account/api-types'
import type { AccountLabels } from '@/components/account/labels'
import type {
  ErpChangeJobSummary,
  ErpChangeProposalSummary,
} from '@/components/account/types'

type ProposalCardProps = {
  labels: AccountLabels
  proposal: ErpChangeProposalSummary
  tenantName: string
  job?: ErpChangeJobSummary
  feedbackApi: FeedbackApi
}

/**
 * A single change proposal with accept / reject review controls. Never renders raw patch JSON or company-map internals.
 */
export function ProposalCard({ labels, proposal, tenantName, feedbackApi }: ProposalCardProps) {
  const { erpFeedbackAction, reviewFeedbackProposal } = feedbackApi

  const acceptBusy = erpFeedbackAction === `proposal-accept:${proposal.id}`
  const rejectBusy = erpFeedbackAction === `proposal-reject:${proposal.id}`
  const reviewable = proposal.status === 'proposed' || proposal.status === 'draft'
  const unsupportedNeed = proposal.unsupportedRuntimeNeeds?.[0]

  return (
    <article className="flex flex-col gap-1.5 rounded-lg border bg-muted/20 p-3">
      <div className="flex flex-col">
        <strong className="text-sm font-semibold">{proposal.title}</strong>
        <span className="text-xs text-muted-foreground">
          {tenantName || 'ERP'} / {proposal.proposalType.replace(/_/g, ' ')} / {proposal.status.replace(/_/g, ' ')}
        </span>
      </div>
      <p className="text-sm">{proposal.summary}</p>
      <span className="text-xs text-muted-foreground">
        {proposal.risk} risk / {proposal.supportedByCurrentRuntime ? 'supported' : labels.unsupportedRuntimeNeed}
      </span>
      {!proposal.supportedByCurrentRuntime && unsupportedNeed ? (
        <p className="text-sm text-muted-foreground">
          {labels.unsupportedRuntimeNeed}: {unsupportedNeed.summary || unsupportedNeed.reason || unsupportedNeed.needType}
        </p>
      ) : null}
      {reviewable ? (
        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            size="sm"
            onClick={() => reviewFeedbackProposal(proposal.id, 'accept')}
            disabled={acceptBusy || rejectBusy}
          >
            {labels.acceptProposal}
          </Button>
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => reviewFeedbackProposal(proposal.id, 'reject')}
            disabled={acceptBusy || rejectBusy}
          >
            {labels.rejectProposal}
          </Button>
        </div>
      ) : null}
    </article>
  )
}
