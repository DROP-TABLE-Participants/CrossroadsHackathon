'use client'

import {
  CaretDown,
  Copy,
  FileText,
  PencilSimple,
  Question,
  Warning,
  ArrowsClockwise as RefreshCw,
} from '@phosphor-icons/react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from '@/components/ui/collapsible'
import { Field, FieldGroup, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'
import type { ProjectsApi } from '@/components/account/api-types'
import {
  humanRoleLabel,
  inviteDeliveryLabel,
  prioritizeCustomerPendingWorkItems,
  resolvedPendingStatuses,
} from '@/components/account/helpers'
import type { AccountLabels } from '@/components/account/labels'
import type { GenerationProjectSummary, GenerationRunSummary } from '@/components/account/types'

type PendingWorkListProps = {
  labels: AccountLabels
  project: GenerationProjectSummary
  items: GenerationRunSummary['pendingWorkItems']
  projectsApi: ProjectsApi
  inlineOnly?: boolean
}

/**
 * A quiet visual signal of what is being asked — a document, a decision, or
 * help — so the card can lead with the actual question instead of repeating
 * the section heading in words.
 */
function kindIcon(kind: string) {
  switch (kind) {
    case 'contradiction_resolution_request':
      return Warning
    case 'artifact_repair_request':
    case 'human_escalation':
      return Question
    default:
      return FileText
  }
}

function kindTone(kind: string) {
  switch (kind) {
    case 'contradiction_resolution_request':
      return 'bg-amber-500/10 text-amber-600 dark:text-amber-500'
    case 'artifact_repair_request':
    case 'human_escalation':
      return 'bg-muted text-muted-foreground'
    default:
      return 'bg-primary/10 text-primary'
  }
}

/**
 * "Needs your attention" — prioritized pending work. Each card leads with the
 * concrete question and hides its forms behind a click, so the list scans as a
 * short to-do rather than a wall of inputs.
 */
export function PendingWorkList({ labels, project, items, projectsApi, inlineOnly = false }: PendingWorkListProps) {
  const {
    pendingAction,
    pendingEvidenceDrafts,
    setPendingEvidenceDrafts,
    pendingResolutionNotes,
    setPendingResolutionNotes,
    inviteLinks,
    sendInterviewInvite,
    copyInterviewInvite,
    addPendingEvidenceDocument,
    resolvePendingWork,
    refreshProjects,
  } = projectsApi

  if (!items.length) return null

  const openItems = prioritizeCustomerPendingWorkItems(items)
    .filter((item) => !resolvedPendingStatuses.has(item.status))
    .slice(0, 4)

  if (!openItems.length) return null

  // The card vanishes the moment an item resolves, so a toast is the only signal
  // the owner gets that their answer landed — and the only place a failure shows.
  const submitEvidence = async (item: (typeof openItems)[number]) => {
    const result = await addPendingEvidenceDocument(project.id, item)
    if (result.ok) {
      toast.success(result.resumed ? labels.continuingGeneration : labels.evidenceSubmitted)
    } else {
      toast.error(result.message || labels.actionFailed)
    }
  }

  const submitAnswer = async (item: (typeof openItems)[number]) => {
    const result = await resolvePendingWork(project.id, item)
    if (result.ok) {
      toast.success(result.resumed ? labels.continuingGeneration : labels.answerSubmitted)
    } else {
      toast.error(result.message || labels.actionFailed)
    }
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-col gap-0.5">
        <div className="flex items-center gap-2">
          <h3 className="text-sm font-semibold">{labels.actionNeeded}</h3>
          <span className="inline-flex h-5 min-w-5 items-center justify-center rounded-full bg-primary/10 px-1.5 text-xs font-semibold text-primary tabular-nums">
            {openItems.length}
          </span>
        </div>
        <p className="text-xs text-muted-foreground">{labels.actionNeededHelper}</p>
      </div>

      <div className="flex flex-col gap-3">
        {openItems.map((item) => {
          const evidenceDraft = pendingEvidenceDrafts[item.id] || { title: '', text: '' }
          const resolutionNote = pendingResolutionNotes[item.id] || ''
          const isEvidence = item.kind === 'evidence_collection_request'
          const isContradiction = item.kind === 'contradiction_resolution_request'
          const KindIcon = kindIcon(item.kind)
          const explainer = isEvidence
            ? labels.evidenceExplainer
            : isContradiction
              ? labels.contradictionExplainer
              : ''
          const linkedRequest = item.linkedInterviewRequestId
            ? project.requests?.find((requestItem) => requestItem.id === item.linkedInterviewRequestId)
            : undefined
          const canResolve = Boolean(
            resolutionNote.trim() || item.linkedInputDocumentId || item.linkedInterviewRequestId,
          )

          return (
            <div className="rounded-lg border bg-card p-4" key={item.id}>
              <div className="flex items-start gap-3">
                <span
                  aria-hidden="true"
                  className={cn(
                    'mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-md',
                    kindTone(item.kind),
                  )}
                >
                  <KindIcon className="size-4" weight="bold" />
                </span>
                <div className="flex min-w-0 flex-col gap-1">
                  <p className="text-sm font-semibold leading-snug">
                    {item.question || item.reason || labels.actionNeeded}
                  </p>
                  {explainer ? (
                    <p className="text-sm text-muted-foreground">{explainer}</p>
                  ) : null}
                  {/* `sourceStage` ("input collection") is the engine's stage key — it
                      told the owner nothing they could act on. Only the role we need
                      an answer from is worth showing here. */}
                  {item.targetRole ? (
                    <p className="text-xs text-muted-foreground">
                      {labels.target}: {humanRoleLabel(item.targetRole)}
                    </p>
                  ) : null}
                </div>
              </div>

              {linkedRequest && !inlineOnly ? (
                <div className="mt-3 flex flex-col gap-2 rounded-md border bg-muted/20 p-3">
                  <p className="text-xs text-muted-foreground">
                    {linkedRequest.inviteDelivery?.recipientEmail
                      ? `${labels.recipient}: ${linkedRequest.inviteDelivery.recipientEmail}`
                      : labels.noEmailKnown}
                    {' · '}
                    {labels.invite}: {inviteDeliveryLabel(labels, linkedRequest.inviteDelivery)}
                  </p>
                  {!linkedRequest.inviteDelivery?.recipientEmail ? (
                    <p className="text-xs text-muted-foreground">{labels.noEmailCopyFallback}</p>
                  ) : null}
                  {linkedRequest.inviteLink || inviteLinks[linkedRequest.id] || linkedRequest.inviteDelivery?.status === 'sent' || !linkedRequest.inviteDelivery?.recipientEmail ? (
                    <div className="flex flex-wrap gap-2">
                      {linkedRequest.inviteLink || inviteLinks[linkedRequest.id] || linkedRequest.inviteDelivery?.status === 'sent' ? (
                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          onClick={() => copyInterviewInvite(linkedRequest.id, linkedRequest.inviteLink || inviteLinks[linkedRequest.id])}
                          disabled={pendingAction === `copy-invite:${linkedRequest.id}`}
                        >
                          <Copy data-icon="inline-start" />
                          {labels.copyInvite}
                        </Button>
                      ) : null}
                      {!linkedRequest.inviteDelivery?.recipientEmail ? (
                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          onClick={() => sendInterviewInvite(linkedRequest.id)}
                          disabled={pendingAction === `invite:${linkedRequest.id}`}
                        >
                          {pendingAction === `invite:${linkedRequest.id}` ? (
                            <Spinner data-icon="inline-start" />
                          ) : (
                            <RefreshCw data-icon="inline-start" />
                          )}
                          {linkedRequest.inviteLink || inviteLinks[linkedRequest.id] ? labels.resendInvite : labels.regenerateInvite}
                        </Button>
                      ) : null}
                    </div>
                  ) : null}
                </div>
              ) : null}

              {!linkedRequest && isContradiction && !inlineOnly ? (
                <div className="mt-3 flex flex-wrap items-center gap-3">
                  <p className="text-sm text-muted-foreground">{labels.interviewBeingPrepared}</p>
                  <Button type="button" size="sm" variant="outline" onClick={refreshProjects}>
                    <RefreshCw data-icon="inline-start" />
                    {labels.refreshProjects}
                  </Button>
                </div>
              ) : null}

              <div className="mt-3 flex flex-col gap-2">
                {isEvidence ? (
                  <Collapsible>
                    <CollapsibleTrigger asChild>
                      <button
                        type="button"
                        className="group flex w-full items-center gap-3 rounded-md border bg-background px-3 py-2.5 text-left transition-colors hover:bg-muted/50"
                      >
                        <FileText className="size-4 shrink-0 text-muted-foreground" />
                        <span className="flex min-w-0 flex-col">
                          <span className="text-sm font-medium">{labels.addEvidenceDocument}</span>
                          <span className="text-xs text-muted-foreground">{labels.addEvidenceHelper}</span>
                        </span>
                        <CaretDown className="ml-auto size-4 shrink-0 text-muted-foreground transition-transform group-data-[state=open]:rotate-180" />
                      </button>
                    </CollapsibleTrigger>
                    <CollapsibleContent>
                      <FieldGroup className="gap-3 pt-3">
                        <Field>
                          <FieldLabel htmlFor={`evidence-title-${item.id}`}>{labels.evidenceTitle}</FieldLabel>
                          <Input
                            id={`evidence-title-${item.id}`}
                            value={evidenceDraft.title}
                            onChange={(event) => setPendingEvidenceDrafts((current) => ({
                              ...current,
                              [item.id]: { ...evidenceDraft, title: event.target.value },
                            }))}
                            placeholder={labels.evidenceTitlePlaceholder}
                          />
                        </Field>
                        <Field>
                          <FieldLabel htmlFor={`evidence-text-${item.id}`}>{labels.evidenceText}</FieldLabel>
                          <Textarea
                            id={`evidence-text-${item.id}`}
                            value={evidenceDraft.text}
                            onChange={(event) => setPendingEvidenceDrafts((current) => ({
                              ...current,
                              [item.id]: { ...evidenceDraft, text: event.target.value },
                            }))}
                            placeholder={labels.evidenceTextPlaceholder}
                            rows={4}
                          />
                        </Field>
                        <div>
                          <Button
                            type="button"
                            size="sm"
                            onClick={() => submitEvidence(item)}
                            disabled={pendingAction === `evidence:${item.id}` || !evidenceDraft.text.trim()}
                          >
                            {pendingAction === `evidence:${item.id}` ? (
                              <Spinner data-icon="inline-start" />
                            ) : (
                              <FileText data-icon="inline-start" />
                            )}
                            {pendingAction === `evidence:${item.id}` ? labels.actionInProgress : labels.submitEvidence}
                          </Button>
                        </div>
                      </FieldGroup>
                    </CollapsibleContent>
                  </Collapsible>
                ) : null}

                <Collapsible defaultOpen={inlineOnly}>
                  <CollapsibleTrigger asChild>
                    <button
                      type="button"
                      className="group flex w-full items-center gap-3 rounded-md border bg-background px-3 py-2.5 text-left transition-colors hover:bg-muted/50"
                    >
                      <PencilSimple className="size-4 shrink-0 text-muted-foreground" />
                      <span className="flex min-w-0 flex-col">
                        <span className="text-sm font-medium">{labels.markResolved}</span>
                        <span className="text-xs text-muted-foreground">{labels.answerHelper}</span>
                      </span>
                      <CaretDown className="ml-auto size-4 shrink-0 text-muted-foreground transition-transform group-data-[state=open]:rotate-180" />
                    </button>
                  </CollapsibleTrigger>
                  <CollapsibleContent>
                    <Field className="pt-3">
                      <FieldLabel htmlFor={`resolution-note-${item.id}`}>{labels.resolutionNote}</FieldLabel>
                      <Textarea
                        id={`resolution-note-${item.id}`}
                        value={resolutionNote}
                        onChange={(event) => setPendingResolutionNotes((current) => ({
                          ...current,
                          [item.id]: event.target.value,
                        }))}
                        placeholder={labels.resolutionNotePlaceholder}
                        rows={3}
                      />
                      <div className="pt-2">
                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          onClick={() => submitAnswer(item)}
                          disabled={pendingAction === `resolve:${item.id}` || !canResolve}
                        >
                          {pendingAction === `resolve:${item.id}` ? <Spinner data-icon="inline-start" /> : null}
                          {pendingAction === `resolve:${item.id}` ? labels.actionInProgress : labels.confirmResolved}
                        </Button>
                      </div>
                    </Field>
                  </CollapsibleContent>
                </Collapsible>
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}
