'use client'

import { Check } from '@phosphor-icons/react'

import { Progress } from '@/components/ui/progress'
import { cn } from '@/lib/utils'

export function Stepper({
  current,
  total,
  stepLabel,
}: {
  current: number
  total: number
  stepLabel: string
}) {
  return (
    <div className="flex w-full flex-col gap-2">
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm font-medium text-muted-foreground" aria-live="polite">
          {stepLabel}
        </p>
        <ol className="flex items-center gap-1.5" aria-hidden="true">
          {Array.from({ length: total }, (_, index) => {
            const step = index + 1
            const done = step < current
            const active = step === current
            return (
              <li
                key={step}
                className={cn(
                  'flex size-6 items-center justify-center rounded-full text-xs font-medium transition-colors',
                  done && 'bg-primary text-primary-foreground',
                  active && 'border-2 border-primary bg-background text-primary',
                  !done && !active && 'border border-border bg-background text-muted-foreground',
                )}
              >
                {done ? <Check className="size-3.5" /> : step}
              </li>
            )
          })}
        </ol>
      </div>
      <Progress value={(current / total) * 100} className="h-1.5" />
    </div>
  )
}
