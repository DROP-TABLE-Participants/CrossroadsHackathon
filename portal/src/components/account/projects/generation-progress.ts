import type { AccountLabels } from '@/components/account/labels'
import type { GenerationProjectSummary, GenerationRunSummary } from '@/components/account/types'

/**
 * Turns the engine's internal stage ledger into something a company owner can
 * read: which of four plain-language phases we're in, and how far along.
 *
 * The compact pipeline currently records company understanding and ERP design.
 * Owners see the same four plain-language phases while the internal stage keys
 * remain an implementation detail.
 */

/**
 * Compact pipeline stages in execution order. Deployment readiness is reported
 * separately by the staging/production status fields.
 */
const STAGE_ORDER: readonly string[] = [
  'company_understanding',
  'erp_design',
]

export type GenerationPhaseKey = 'understanding' | 'questions' | 'building' | 'reviewing'

/** Which owner-facing phase each engine stage belongs to. */
const STAGE_PHASE: Record<string, GenerationPhaseKey> = {
  company_understanding: 'understanding',
  erp_design: 'building',
}

/** Stages that mean "stopped, waiting for a person" — not forward progress. */
const RUNNING_STATUSES = new Set(['queued', 'running', 'resuming', 'resume_failed', 'cancellation_requested'])
const WAITING_STATUSES = new Set(['waiting_for_input'])
const DONE_STATUSES = new Set(['completed', 'staging_ready', 'production_ready'])

export type GenerationProgress = {
  /** 0-100, monotonic: reflects the furthest stage reached, never a guess. */
  percent: number
  phase: GenerationPhaseKey
  /** Plain-language description of what is happening right now. */
  headline: string
  tone: 'running' | 'waiting' | 'done' | 'failed' | 'cancelled'
}

function furthestStageIndex(run: GenerationRunSummary): number {
  let furthest = -1
  for (const entry of run.stages || []) {
    const index = STAGE_ORDER.indexOf(entry.stage)
    if (index > furthest) furthest = index
  }
  return furthest
}

export function phaseLabel(labels: AccountLabels, phase: GenerationPhaseKey): string {
  switch (phase) {
    case 'understanding':
      return labels.phaseUnderstanding
    case 'questions':
      return labels.phaseQuestions
    case 'building':
      return labels.phaseBuilding
    default:
      return labels.phaseReviewing
  }
}

/**
 * Returns null when there is nothing truthful to show — no run yet, so no
 * progress bar. An empty state is better than a fake 0%.
 */
export function generationProgress(
  labels: AccountLabels,
  project: GenerationProjectSummary,
  run: GenerationRunSummary | undefined,
): GenerationProgress | null {
  if (!run) return null

  const stageIndex = furthestStageIndex(run)
  const phase = stageIndex >= 0
    ? STAGE_PHASE[STAGE_ORDER[stageIndex]] ?? 'understanding'
    : 'understanding'

  if (
    ['pending', 'deploying'].includes(run.staging?.status || '') ||
    (project.status === 'generated' && run.status === 'completed' && !run.staging)
  ) {
    return {
      percent: 99,
      phase: 'building',
      headline: labels.stagingChange,
      tone: 'running',
    }
  }

  if (run.staging?.status === 'failed') {
    return {
      percent: 99,
      phase: 'building',
      headline: labels.stagingFailed,
      tone: 'failed',
    }
  }

  if (DONE_STATUSES.has(run.status)) {
    return { percent: 100, phase: 'reviewing', headline: labels.progressDoneHeadline, tone: 'done' }
  }

  if (run.status === 'failed') {
    return {
      percent: Math.max(0, Math.round(((stageIndex + 1) / STAGE_ORDER.length) * 100)),
      phase,
      headline: labels.progressFailedHeadline,
      tone: 'failed',
    }
  }

  if (run.status === 'cancelled') {
    return {
      percent: Math.max(0, Math.round(((stageIndex + 1) / STAGE_ORDER.length) * 100)),
      phase,
      headline: labels.progressCancelledHeadline,
      tone: 'cancelled',
    }
  }

  if (run.status === 'cancellation_requested') {
    return {
      percent: Math.max(2, Math.round(((stageIndex + 1) / STAGE_ORDER.length) * 100)),
      phase,
      headline: labels.progressCancellingHeadline,
      tone: 'waiting',
    }
  }

  if (run.status === 'resuming') {
    return {
      percent: Math.max(5, Math.round(((stageIndex + 1) / STAGE_ORDER.length) * 100)),
      phase: 'questions',
      headline: labels.continuingGeneration,
      tone: 'running',
    }
  }

  if (run.status === 'resume_failed') {
    return {
      percent: Math.max(5, Math.round(((stageIndex + 1) / STAGE_ORDER.length) * 100)),
      phase: 'questions',
      headline: labels.progressResumeFailedHeadline,
      tone: 'failed',
    }
  }

  // Waiting on a person: hold the bar where it is and say who we're waiting for,
  // rather than implying the machine is still working.
  const waitingOnPerson = WAITING_STATUSES.has(run.status)
  if (waitingOnPerson) {
    const awaitingInterview = run.pendingWorkItems?.some((item) =>
      item.kind === 'interview_request' && item.status !== 'resolved')
    return {
      percent: Math.max(5, Math.round(((stageIndex + 1) / STAGE_ORDER.length) * 100)),
      phase,
      headline: awaitingInterview ? labels.progressWaitingInterview : labels.progressWaitingEvidence,
      tone: 'waiting',
    }
  }

  if (RUNNING_STATUSES.has(run.status)) {
    // A queued run that has not logged a stage yet is genuinely at the start.
    const percent = stageIndex < 0 ? 2 : Math.round(((stageIndex + 1) / STAGE_ORDER.length) * 100)
    return {
      percent: Math.min(99, Math.max(2, percent)),
      phase,
      headline: phaseHeadline(labels, phase),
      tone: 'running',
    }
  }

  return {
    percent: Math.max(0, Math.round(((stageIndex + 1) / STAGE_ORDER.length) * 100)),
    phase,
    headline: phaseHeadline(labels, phase),
    tone: 'running',
  }
}

function phaseHeadline(labels: AccountLabels, phase: GenerationPhaseKey): string {
  switch (phase) {
    case 'understanding':
      return labels.progressUnderstandingHeadline
    case 'questions':
      return labels.progressQuestionsHeadline
    case 'building':
      return labels.progressBuildingHeadline
    default:
      return labels.progressReviewingHeadline
  }
}

/**
 * True while the durable pipeline holds the project — connectors must not
 * change underneath a run that has already collected its evidence.
 */
export function generationInFlight(run: GenerationRunSummary | undefined): boolean {
  if (!run) return false
  return RUNNING_STATUSES.has(run.status) || run.staging?.status === 'deploying' || run.production?.status === 'promoting'
}
