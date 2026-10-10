'use client'

import { CaretRight as ChevronRight } from '@phosphor-icons/react'

import { Button } from '@/components/ui/button'
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from '@/components/ui/collapsible'
import type { AccountLabels } from '@/components/account/labels'
import type { GenerationRunSummary } from '@/components/account/types'

type RunTimelineProps = {
  labels: AccountLabels
  stages: GenerationRunSummary['stages']
}

/**
 * Technical run stages, collapsed by default. Muted and small on purpose —
 * the owner-facing story lives in the milestones above.
 */
export function RunTimeline({ labels, stages }: RunTimelineProps) {
  if (!stages.length) return null

  return (
    <Collapsible>
      <CollapsibleTrigger asChild>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className="-ml-2 w-fit text-muted-foreground group/timeline"
        >
          <ChevronRight
            data-icon="inline-start"
            className="transition-transform group-aria-expanded/timeline:rotate-90"
            aria-hidden="true"
          />
          {labels.technicalProgress}
        </Button>
      </CollapsibleTrigger>
      <CollapsibleContent>
        <ul className="flex flex-col gap-1 pt-1 pl-2 text-xs text-muted-foreground">
          {stages.slice(-5).map((stage) => (
            <li key={stage.id}>
              {stage.stage.replace(/_/g, ' ')} / {stage.status.replace(/_/g, ' ')}
              {stage.summary ? ` / ${stage.summary}` : ''}
            </li>
          ))}
        </ul>
      </CollapsibleContent>
    </Collapsible>
  )
}
