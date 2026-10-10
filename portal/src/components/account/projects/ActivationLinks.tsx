'use client'

import { Copy, ArrowSquareOut as ExternalLink, ArrowsClockwise as RefreshCw } from '@phosphor-icons/react'

import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import type { AccountLabels } from '@/components/account/labels'
import type { OneTimeActivationLink, RuntimeActivationSummary } from '@/components/account/types'

export function activationStatusLabel(labels: AccountLabels, activation?: RuntimeActivationSummary) {
  switch (activation?.status) {
    case 'activated':
      return labels.activationActivated
    case 'expired':
    case 'revoked':
      return labels.activationExpired
    case 'pending':
      return labels.activationPending
    default:
      return labels.activationNotCreated
  }
}

type ActivationLinksProps = {
  labels: AccountLabels
  activation?: RuntimeActivationSummary
  activationLink?: OneTimeActivationLink
  activateLabel: string
  busy: boolean
  onRegenerate: () => void
}

/**
 * Owner-activation block shown inside a ready staging/production milestone:
 * explains the separate runtime account, reveals the one-time activation link,
 * and lets the owner copy or regenerate it.
 */
export function ActivationLinks({
  labels,
  activation,
  activationLink,
  activateLabel,
  busy,
  onRegenerate,
}: ActivationLinksProps) {
  return (
    <div className="flex flex-col gap-2 rounded-md border border-dashed bg-background p-2.5">
      <p className="text-xs text-muted-foreground">{labels.runtimeActivationNotice}</p>
      <span className="text-xs">
        {labels.ownerActivation}: {activationStatusLabel(labels, activation)}
        {activation?.activationEmail ? ` / ${activation.activationEmail}` : ''}
      </span>
      {activationLink ? (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" asChild>
            <a href={activationLink.activationUrl} target="_blank" rel="noreferrer">
              <ExternalLink data-icon="inline-start" />
              {activateLabel}
            </a>
          </Button>
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => navigator.clipboard?.writeText(activationLink.activationUrl)}
          >
            <Copy data-icon="inline-start" />
            {labels.copyActivationLink}
          </Button>
        </div>
      ) : null}
      {activation?.status !== 'activated' ? (
        <div>
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={onRegenerate}
            disabled={busy}
          >
            {busy ? <Spinner data-icon="inline-start" /> : <RefreshCw data-icon="inline-start" />}
            {labels.regenerateActivationLink}
          </Button>
        </div>
      ) : null}
    </div>
  )
}
