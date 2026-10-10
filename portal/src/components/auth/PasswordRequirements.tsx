'use client'

import { Check } from '@phosphor-icons/react'

import { cn } from '@/lib/utils'
import {
  evaluatePassword,
  PASSWORD_RULE_ORDER,
  type PasswordRuleId,
} from '@/lib/auth/password-rules'

/**
 * Live password checklist. Each rule turns green with a check as it's satisfied
 * (immediate feedback — one of the core UX principles). Uses aria-live so screen
 * readers announce a rule flipping to satisfied.
 */
export function PasswordRequirements({
  password,
  title,
  labels,
}: {
  password: string
  title: string
  labels: Record<PasswordRuleId, string>
}) {
  const results = evaluatePassword(password)

  return (
    <div className="mt-3">
      <p className="text-sm font-medium text-foreground">{title}</p>
      <ul className="mt-2 space-y-1.5" aria-live="polite">
        {PASSWORD_RULE_ORDER.map((id) => {
          const ok = results[id]
          return (
            <li key={id} className="flex items-start gap-2 text-sm">
              <Check
                weight="bold"
                aria-hidden
                className={cn(
                  'mt-0.5 size-4 shrink-0 transition-colors',
                  ok ? 'text-success' : 'text-muted-foreground/35',
                )}
              />
              <span className={cn('transition-colors', ok ? 'text-foreground' : 'text-muted-foreground')}>
                {labels[id]}
                <span className="sr-only">{ok ? ' — met' : ' — not met'}</span>
              </span>
            </li>
          )
        })}
      </ul>
    </div>
  )
}
