'use client'

import { CheckCircle, Copy, EnvelopeSimple, Hourglass, PaperPlaneTilt } from '@phosphor-icons/react'
import { toast } from 'sonner'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import type { ProjectsApi } from '@/components/account/api-types'
import { humanRoleLabel } from '@/components/account/helpers'
import type { AccountLabels } from '@/components/account/labels'
import type { GenerationProjectSummary, GenerationRunSummary } from '@/components/account/types'

type InterviewRequest = NonNullable<GenerationProjectSummary['requests']>[number]

/** Pending-work statuses that mean the question no longer needs an answer. */
const resolvedStatuses = new Set(['resolved', 'completed', 'cancelled'])

type InterviewState = {
  label: string
  variant: 'secondary' | 'outline' | 'default'
  Icon: typeof CheckCircle
}

/**
 * What the owner actually wants to know about an interview: who was asked, and
 * whether they have answered yet. Never the questionSetKey — that is an engine
 * identifier (e.g. "pending_work_the_connected_sources_did_not_contain_enough…")
 * and it leaked straight onto the customer's screen before this.
 */
function interviewState(labels: AccountLabels, request: InterviewRequest): InterviewState {
  if (request.completedAt || request.status === 'completed') {
    return { label: labels.interviewCompleted, variant: 'default', Icon: CheckCircle }
  }
  if (request.inviteDelivery?.status === 'sent' || request.sentAt) {
    return { label: labels.interviewAwaitingReply, variant: 'secondary', Icon: Hourglass }
  }
  return { label: labels.interviewNotSentYet, variant: 'outline', Icon: EnvelopeSimple }
}

function participantName(project: GenerationProjectSummary, request: InterviewRequest): string | undefined {
  const participant = project.participants?.find((candidate) => candidate.id === request.participantId)
  if (!participant) return request.inviteDelivery?.recipientEmail
  const role = participant.roleTitle ? humanRoleLabel(participant.roleTitle) : ''
  return role ? `${participant.name} — ${role}` : participant.name
}

export function InterviewsPanel({
  labels,
  project,
  projectsApi,
  pendingWorkItems = [],
}: {
  labels: AccountLabels
  project: GenerationProjectSummary
  projectsApi: ProjectsApi
  /** Used only to surface the question the agent is waiting on. */
  pendingWorkItems?: GenerationRunSummary['pendingWorkItems']
}) {
  const { pendingAction, inviteLinks, sendInterviewInvite, copyInterviewInvite } = projectsApi
  const requests = project.requests || []
  if (!requests.length) return null

  const questionFor = (requestId: string) => pendingWorkItems
    .find((item) => item.linkedInterviewRequestId === requestId && !resolvedStatuses.has(item.status))
    ?.question

  return (
    <section data-slot="interviews-panel" className="flex flex-col gap-3 rounded-lg border p-4">
      <div className="flex flex-col gap-0.5">
        <h3 className="text-base font-semibold">{labels.interviews}</h3>
        <p className="text-sm text-muted-foreground">{labels.interviewsHelper}</p>
      </div>

      <ul className="flex flex-col gap-2">
        {requests.map((request) => {
          const state = interviewState(labels, request)
          const who = participantName(project, request)
          const link = request.inviteLink || inviteLinks[request.id]
          const canCopy = Boolean(link) || request.inviteDelivery?.status === 'sent'
          const sending = pendingAction === `invite:${request.id}`

          const question = questionFor(request.id)

          return (
            <li
              key={request.id}
              className="flex flex-col gap-2.5 rounded-lg border bg-card p-4"
            >
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="flex min-w-0 items-center gap-2.5">
                  <state.Icon aria-hidden="true" className="size-4.5 shrink-0 text-muted-foreground" />
                  <div className="flex min-w-0 flex-col">
                    <strong className="truncate text-base font-semibold">{who || labels.interviews}</strong>
                    {request.inviteDelivery?.recipientEmail && who !== request.inviteDelivery.recipientEmail ? (
                      <span className="truncate text-sm text-muted-foreground">
                        {request.inviteDelivery.recipientEmail}
                      </span>
                    ) : null}
                  </div>
                </div>
                <Badge variant={state.variant}>{state.label}</Badge>
              </div>

              {/* The agent's question can run to a paragraph. Clamp it — it is
                  context, not the thing the owner is here to act on. */}
              {question ? (
                <p className="line-clamp-2 text-sm leading-relaxed text-muted-foreground" title={question}>
                  {question}
                </p>
              ) : null}

              <div className="flex flex-wrap items-center gap-2">
                {canCopy ? (
                  <Button
                    type="button"
                    size="sm"
                    onClick={async () => {
                      const copied = await copyInterviewInvite(request.id, link)
                      if (copied) toast.success(labels.inviteLinkCopied)
                      else toast.error(labels.inviteLinkCopyFailed)
                    }}
                    disabled={pendingAction === `copy-invite:${request.id}`}
                  >
                    <Copy data-icon="inline-start" />
                    {labels.copyInvite}
                  </Button>
                ) : null}
                {!request.inviteDelivery?.recipientEmail ? (
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => sendInterviewInvite(request.id)}
                    disabled={sending}
                  >
                    {sending ? <Spinner data-icon="inline-start" /> : <PaperPlaneTilt data-icon="inline-start" />}
                    {link ? labels.resendInvite : labels.regenerateInvite}
                  </Button>
                ) : null}
              </div>
            </li>
          )
        })}
      </ul>
    </section>
  )
}
