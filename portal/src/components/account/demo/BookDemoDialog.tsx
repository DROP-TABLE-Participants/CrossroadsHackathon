'use client'

import Cal, { getCalApi } from '@calcom/embed-react'
import { useEffect } from 'react'

import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import type { DemoHost } from '@/components/account/demo/hosts'
import type { AccountLabels } from '@/components/account/labels'
import type { Locale } from '@/i18n/config'

const CAL_NAMESPACE = 'erpgenerator-demo'

type BookDemoDialogProps = {
  labels: AccountLabels
  host: DemoHost | null
  onOpenChange: (open: boolean) => void
  /** Prefilled so the customer is not retyping what we already know. */
  name?: string
  email?: string
}

/**
 * Cal.com's own calendar, in a ERP Generator dialog.
 *
 * The embed is themed to brand teal and light mode explicitly: it renders in
 * an iframe, so our tokens cannot cascade into it and it would otherwise follow
 * the viewer's OS dark preference on a panel that has no dark mode.
 */
export function BookDemoDialog({ labels, host, onOpenChange, name, email }: BookDemoDialogProps) {
  // A namespace per host, not one shared "erpgenerator-demo". Cal caches an embed
  // connection per namespace — including the origin it first bound to — so once
  // Stoyan's cal.eu link claimed the shared namespace, the cal.com hosts (Mario,
  // Egor) reusing it silently failed to load against the wrong origin. Distinct
  // namespaces keep each origin isolated.
  const namespace = host ? `${CAL_NAMESPACE}-${host.id}` : CAL_NAMESPACE

  useEffect(() => {
    if (!host) return
    let cancelled = false

    ;(async () => {
      const cal = await getCalApi({ namespace })
      if (cancelled) return
      cal('ui', {
        theme: 'light',
        // Both themes carry the brand teal even though the embed is pinned to
        // light: the type demands the pair, and if the pin ever lifts, the
        // fallback should still be ERP Generator rather than Cal's default.
        cssVarsPerTheme: {
          light: { 'cal-brand': '#0f766e' },
          dark: { 'cal-brand': '#0f766e' },
        },
        hideEventTypeDetails: false,
        layout: 'month_view',
      })
    })()

    return () => {
      cancelled = true
    }
  }, [namespace, host])

  return (
    <Dialog open={Boolean(host)} onOpenChange={onOpenChange}>
      {/* Wide on purpose. Cal's Booker stacks the time slots *below* the month
          grid in a narrow container, which pushed the times off-screen and made
          scrolling mandatory to do the one thing this dialog is for. Given room
          it lays out event details | calendar | times side by side, which fits
          without scrolling and is the layout people recognise from cal.com. */}
      <DialogContent className="max-h-[92vh] w-[min(72rem,95vw)] gap-0 overflow-hidden p-0 sm:max-w-[72rem]">
        {/* Announced, not drawn. Cal's own embed already leads with the host,
            the event name and its length, so a visible header here would say
            the person's name twice and push the calendar — the only thing
            anyone opened this for — down the screen. The title still has to
            exist: Radix requires one to label the dialog. */}
        <DialogHeader className="sr-only">
          <DialogTitle>{host ? labels.demoBookWith(host.name) : labels.demoBookTitle}</DialogTitle>
          <DialogDescription>{labels.demoBookDialogHelper}</DialogDescription>
        </DialogHeader>

        {/* Remounted per host: Cal binds its link at mount, so reusing one
            instance would keep showing the first person picked. */}
        <div className="max-h-[90vh] overflow-y-auto">
          {host ? (
            <Cal
              key={host.id}
              namespace={namespace}
              calLink={host.calLink}
              calOrigin={host.calOrigin}
              config={{
                layout: 'month_view',
                theme: 'light',
                ...(name ? { name } : {}),
                ...(email ? { email } : {}),
              }}
              // Height must stay `auto`: Cal's embed measures its own content
              // and grows the iframe (a month view plus time slots runs well
              // past 1700px). Pinning it to 100% clipped the calendar to the
              // dialog and left nothing to scroll — the time slots for a chosen
              // day were simply unreachable. Let it grow; the wrapper scrolls.
              style={{ width: '100%', height: 'auto', overflow: 'visible' }}
            />
          ) : null}
        </div>
      </DialogContent>
    </Dialog>
  )
}
