'use client'

import { ArrowSquareOut as ExternalLink, Rocket } from '@phosphor-icons/react'

import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
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
 * A single change proposal: accept / reject review controls, automatic
 * staging status, and — once staged — a strictly confirmed apply-to-production
 * path. Never renders raw patch JSON or company-map internals.
 */
export function ProposalCard({ labels, proposal, tenantName, job, feedbackApi }: ProposalCardProps) {
  const { erpFeedbackAction, reviewFeedbackProposal, applyChangeJobProduction } = feedbackApi

  const acceptBusy = erpFeedbackAction === `proposal-accept:${proposal.id}`
  const rejectBusy = erpFeedbackAction === `proposal-reject:${proposal.id}`
  const applyBusy = job ? erpFeedbackAction === `change-job-apply:${job.id}` : false
  const productionApplyStatus = job?.productionApplyStatus || 'not_requested'
  const reviewable = proposal.status === 'proposed' || proposal.status === 'draft'
  const automaticStagingActive = proposal.stagingAutomation?.status === 'queued' ||
    proposal.stagingAutomation?.status === 'claimed'
  const canApplyProduction = Boolean(job) &&
    job?.status === 'staged_for_review' &&
    productionApplyStatus !== 'applied' &&
    productionApplyStatus !== 'applying' &&
    proposal.supportedByCurrentRuntime
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
      {automaticStagingActive ? (
        <p className="text-sm text-muted-foreground" role="status">{labels.stagingChange}</p>
      ) : null}
      {job ? (
        <div className="flex flex-col gap-1.5 rounded-md border bg-background p-3">
          <strong className="text-sm font-semibold">
            {job.status === 'staged_for_review'
              ? labels.stagedForReview
              : job.status === 'staging_failed' || job.status === 'compile_failed' || job.status === 'failed'
                ? labels.stagingFailed
                : job.status.replace(/_/g, ' ')}
          </strong>
          {job.patchSummary ? <p className="text-sm text-muted-foreground">{job.patchSummary}</p> : null}
          {job.patchedBundleVersion ? (
            <span className="text-xs text-muted-foreground">{labels.stagedBundle}: {job.patchedBundleVersion}</span>
          ) : null}
          {job.safeError ? <p className="text-xs text-destructive">{job.safeError}</p> : null}
          {job.status === 'staged_for_review' ? (
            <>
              <span className="text-xs text-muted-foreground">{labels.productionUnchanged}</span>
              {job.stagingUrl ? (
                <div>
                  <Button size="sm" variant="outline" asChild>
                    <a href={job.stagingUrl} target="_blank" rel="noreferrer">
                      <ExternalLink data-icon="inline-start" />
                      {labels.openStagingErp}
                    </a>
                  </Button>
                </div>
              ) : null}
              {productionApplyStatus === 'not_requested' || productionApplyStatus === 'failed' ? (
                <>
                  <p className="text-sm text-muted-foreground">{labels.productionApplyWarning}</p>
                  <div>
                    <Button
                      type="button"
                      size="sm"
                      onClick={() => applyChangeJobProduction(job.id)}
                      disabled={!canApplyProduction || applyBusy}
                    >
                      {applyBusy ? <Spinner data-icon="inline-start" /> : <Rocket data-icon="inline-start" />}
                      {applyBusy ? labels.applyingProduction : labels.applyProduction}
                    </Button>
                  </div>
                </>
              ) : null}
              {productionApplyStatus === 'applying' ? (
                <span className="text-xs text-muted-foreground">{labels.applyingProduction}</span>
              ) : null}
              {productionApplyStatus === 'applied' ? (
                <>
                  <strong className="text-sm font-semibold">{labels.appliedToProduction}</strong>
                  {job.productionBundleVersion ? (
                    <span className="text-xs text-muted-foreground">{labels.activeBundle}: {job.productionBundleVersion}</span>
                  ) : null}
                  {job.productionUrl ? (
                    <div>
                      <Button size="sm" variant="outline" asChild>
                        <a href={job.productionUrl} target="_blank" rel="noreferrer">
                          <ExternalLink data-icon="inline-start" />
                          {labels.openProductionErp}
                        </a>
                      </Button>
                    </div>
                  ) : null}
                </>
              ) : null}
              {productionApplyStatus === 'blocked_stale_base' ? (
                <p className="text-xs text-destructive">{labels.productionChangedRestage}</p>
              ) : null}
              {productionApplyStatus === 'failed' ? (
                <p className="text-xs text-destructive">{job.productionSafeError || labels.productionApplyFailed}</p>
              ) : null}
            </>
          ) : null}
        </div>
      ) : null}
    </article>
  )
}
