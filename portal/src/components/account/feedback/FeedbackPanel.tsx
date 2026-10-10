'use client'

import { ChatsCircle, ClipboardText as ClipboardList, ArrowsClockwise as RefreshCw } from '@phosphor-icons/react'
import type { ReactNode } from 'react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from '@/components/ui/empty'
import { humanRoleLabel } from '@/components/account/helpers'
import type { FeedbackApi, TenantsApi } from '@/components/account/api-types'
import type { AccountLabels } from '@/components/account/labels'
import { ObservationCard } from './ObservationCard'
import { ProposalCard } from './ProposalCard'

type FeedbackPanelProps = {
  labels: AccountLabels
  feedbackApi: FeedbackApi
  tenantsApi: TenantsApi
  /**
   * 'collapsible' (default) renders the legacy <details> block used by the
   * stacked account page; 'page' renders the same content expanded with a
   * page-level heading for the dedicated /account/feedback route.
   */
  layout?: 'collapsible' | 'page'
}

/** A column heading that carries its own count, so no row has to say ": 0". */
function ColumnHeading({ title, count }: { title: string; count: number }) {
  return (
    <div className="flex items-center gap-2">
      <h4 className="text-base font-semibold">{title}</h4>
      {count > 0 ? (
        <Badge variant="secondary" className="tabular-nums">
          {count}
        </Badge>
      ) : null}
    </div>
  )
}

function Column({ title, count, children }: { title: string; count: number; children: ReactNode }) {
  return (
    <section aria-label={title} className="flex flex-col gap-2">
      <ColumnHeading title={title} count={count} />
      {children}
    </section>
  )
}

/**
 * "Improvements" — once an ERP is live, ERP Generator watches how it is used and
 * suggests changes; the owner reviews and confirms them here.
 */
export function FeedbackPanel({ labels, feedbackApi, tenantsApi, layout = 'collapsible' }: FeedbackPanelProps) {
  const {
    erpFeedbackItems,
    erpUsageObservations,
    erpChangeProposals,
    erpChangeJobs,
    erpFeedbackLoading,
    erpFeedbackNotice,
    refreshErpFeedback,
  } = feedbackApi
  const { tenants } = tenantsApi

  const tenantNameFor = (tenantId?: string) =>
    tenants.find((candidate) => candidate.id === tenantId)?.companyName || ''

  const visibleObservations = erpUsageObservations.filter((observation) => observation.status !== 'dismissed')
  const nothingYet = !erpFeedbackItems.length && !visibleObservations.length && !erpChangeProposals.length

  const refreshButton = (
    <Button
      type="button"
      size="icon"
      variant="ghost"
      onClick={refreshErpFeedback}
      disabled={erpFeedbackLoading}
      aria-label={labels.refreshTenants}
      title={labels.refreshTenants}
    >
      <RefreshCw className={erpFeedbackLoading ? 'animate-spin' : undefined} />
    </Button>
  )

  const body = (
    <>
      {erpFeedbackNotice ? (
        <p className="text-sm text-primary" role="status">{erpFeedbackNotice}</p>
      ) : null}

      {/* Before an ERP has been used there is nothing in any of the three
          columns, and the page was three headings each reporting zero. One
          explanation of what will land here beats three ways of saying nothing. */}
      {nothingYet ? (
        <Empty className="rounded-lg border p-4">
          <EmptyHeader>
            <EmptyMedia variant="icon">
              <ChatsCircle aria-hidden="true" />
            </EmptyMedia>
            <EmptyTitle>{labels.feedbackNothingYet}</EmptyTitle>
            <EmptyDescription>{labels.feedbackNothingYetHelper}</EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (
        <>
          <div className="grid gap-5 lg:grid-cols-3">
            <Column title={labels.recentFeedback} count={erpFeedbackItems.length}>
              {erpFeedbackItems.length ? (
                <div className="flex flex-col gap-2">
                  {erpFeedbackItems.slice(0, 8).map((item) => (
                    <article className="flex flex-col gap-1.5 rounded-lg border bg-muted/20 p-3" key={item.id}>
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        {/* `category` and `status` are engine enums
                            ("records_without_text"); neither is shown raw. */}
                        <strong className="text-sm font-semibold">
                          {humanRoleLabel(item.category) || labels.recentFeedback}
                        </strong>
                        <Badge variant="outline">{humanRoleLabel(item.status)}</Badge>
                      </div>
                      <p className="text-sm">{item.message}</p>
                      <span className="text-xs text-muted-foreground">
                        {tenantNameFor(item.tenantId) || item.sourceEnvironment || 'ERP'}
                      </span>
                    </article>
                  ))}
                </div>
              ) : (
                <p className="text-sm text-muted-foreground">{labels.noFeedback}</p>
              )}
            </Column>

            <Column title={labels.usageObservations} count={visibleObservations.length}>
              {visibleObservations.length ? (
                <div className="flex flex-col gap-2">
                  {visibleObservations.slice(0, 8).map((observation) => {
                    const proposal = observation.proposalId
                      ? erpChangeProposals.find((candidate) => candidate.id === observation.proposalId)
                      : erpChangeProposals.find((candidate) => candidate.usageObservationId === observation.id)
                    return (
                      <ObservationCard
                        key={observation.id}
                        labels={labels}
                        observation={observation}
                        tenantName={tenantNameFor(observation.tenantId)}
                        proposal={proposal}
                        feedbackApi={feedbackApi}
                      />
                    )
                  })}
                </div>
              ) : (
                <p className="text-sm text-muted-foreground">{labels.noObservations}</p>
              )}
            </Column>

            <Column title={labels.changeProposals} count={erpChangeProposals.length}>
              {erpChangeProposals.length ? (
                <div className="flex flex-col gap-2">
                  {erpChangeProposals.slice(0, 8).map((proposal) => (
                    <ProposalCard
                      key={proposal.id}
                      labels={labels}
                      proposal={proposal}
                      tenantName={tenantNameFor(proposal.tenantId)}
                      job={erpChangeJobs.find((candidate) => candidate.proposalId === proposal.id)}
                      feedbackApi={feedbackApi}
                    />
                  ))}
                </div>
              ) : (
                <p className="text-sm text-muted-foreground">{labels.noProposals}</p>
              )}
            </Column>
          </div>

          {/* The safety promise belongs next to the thing it is about. Shown
              permanently it was reassurance about a decision nobody had been
              asked to make yet. */}
          {erpChangeProposals.length ? (
            <p className="border-t pt-4 text-xs text-muted-foreground">{labels.acceptedNotApplied}</p>
          ) : null}
        </>
      )}
    </>
  )

  if (layout === 'page') {
    return (
      <section aria-label={labels.feedbackImprovements} className="flex flex-col gap-4">
        <div className="flex items-start justify-between gap-3">
          <div className="flex flex-col gap-1">
            <h1 className="text-2xl font-semibold">{labels.feedbackImprovements}</h1>
          </div>
          {refreshButton}
        </div>
        <div className="flex flex-col gap-4">{body}</div>
      </section>
    )
  }

  return (
    <section aria-label={labels.feedbackImprovements}>
      <details className="rounded-xl border bg-card">
        <summary className="flex cursor-pointer list-none items-center gap-2 p-4 [&::-webkit-details-marker]:hidden">
          <ClipboardList className="size-5 text-muted-foreground" aria-hidden="true" />
          <h3 className="text-base font-semibold">{labels.feedbackImprovements}</h3>
          <span className="ml-auto text-xs text-muted-foreground">{labels.afterErpReady}</span>
        </summary>
        <div className="flex flex-col gap-4 border-t p-4">
          <div className="flex justify-end">{refreshButton}</div>
          {body}
        </div>
      </details>
    </section>
  )
}
