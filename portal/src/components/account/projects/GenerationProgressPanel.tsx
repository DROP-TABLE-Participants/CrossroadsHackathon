'use client'

import { CheckCircle, Warning, Hourglass, XCircle } from '@phosphor-icons/react'

import { Progress } from '@/components/ui/progress'
import { Spinner } from '@/components/ui/spinner'
import { cn } from '@/lib/utils'
import type { AccountLabels } from '@/components/account/labels'
import type { GenerationProjectSummary, GenerationRunSummary } from '@/components/account/types'
import { generationProgress, phaseLabel, type GenerationPhaseKey } from './generation-progress'

const PHASE_ORDER: readonly GenerationPhaseKey[] = ['understanding', 'questions', 'building', 'reviewing']

/**
 * "Where things stand": the one place an owner looks to know what ERP Generator is
 * doing and how far along it is. Deliberately says nothing about stages, runs,
 * models or modes — position and plain language only.
 */
export function GenerationProgressPanel({
  labels,
  project,
  run,
}: {
  labels: AccountLabels
  project: GenerationProjectSummary
  run: GenerationRunSummary | undefined
}) {
  const progress = generationProgress(labels, project, run)
  if (!progress) return null

  const phaseIndex = PHASE_ORDER.indexOf(progress.phase)
  const StatusIcon = progress.tone === 'done'
    ? CheckCircle
    : progress.tone === 'failed'
      ? Warning
      : progress.tone === 'cancelled'
        ? XCircle
        : progress.tone === 'waiting'
          ? Hourglass
          : null

  return (
    <section
      data-slot="generation-progress"
      aria-label={labels.progressTitle}
      className="flex flex-col gap-2"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-base font-semibold">{phaseLabel(labels, progress.phase)}</h3>
        <span className="text-xs text-muted-foreground tabular-nums">
          {labels.progressStepOf(Math.max(1, phaseIndex + 1), PHASE_ORDER.length)}
        </span>
      </div>

      <div className="flex flex-col gap-2">
        <div className="flex items-baseline justify-between gap-3">
          <span
            className={cn(
              'ml-auto text-sm font-semibold tabular-nums',
              progress.tone === 'failed' ? 'text-destructive' : 'text-primary',
            )}
          >
            {progress.percent}%
          </span>
        </div>
        <Progress
          value={progress.percent}
          aria-label={`${phaseLabel(labels, progress.phase)} — ${progress.percent}%`}
          className={cn(progress.tone === 'failed' && '[&>[data-slot=progress-indicator]]:bg-destructive')}
        />
      </div>

      <p className="flex items-start gap-2 text-sm text-muted-foreground">
        {StatusIcon ? (
          <StatusIcon
            aria-hidden="true"
            className={cn(
              'mt-0.5 size-4 shrink-0',
              progress.tone === 'done' && 'text-success',
              progress.tone === 'failed' && 'text-destructive',
              progress.tone === 'cancelled' && 'text-muted-foreground',
            )}
          />
        ) : (
          <Spinner aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
        )}
        {progress.headline}
      </p>
    </section>
  )
}
