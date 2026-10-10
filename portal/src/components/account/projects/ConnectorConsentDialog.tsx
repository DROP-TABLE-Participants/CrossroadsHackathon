'use client'

import { useState } from 'react'
import { ShieldCheck } from '@phosphor-icons/react'

import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import { Spinner } from '@/components/ui/spinner'
import type { AccountLabels } from '@/components/account/labels'
import type { ConnectorOption } from '@/components/account/types'

type ConnectorConsentDialogProps = {
  labels: AccountLabels
  connector: ConnectorOption | null
  busy: boolean
  onOpenChange: (open: boolean) => void
  onConfirm: (connector: ConnectorOption) => Promise<void>
}

export function ConnectorConsentDialog({
  labels,
  connector,
  busy,
  onOpenChange,
  onConfirm,
}: ConnectorConsentDialogProps) {
  const [confirmed, setConfirmed] = useState(false)

  return (
    <Dialog
      open={Boolean(connector)}
      onOpenChange={(open) => {
        if (!open) setConfirmed(false)
        onOpenChange(open)
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <ShieldCheck className="size-5 text-primary" aria-hidden="true" />
            {labels.connectorDataNoticeTitle}
          </DialogTitle>
          <DialogDescription>{labels.connectorDataNoticeDescription}</DialogDescription>
        </DialogHeader>
        <ul className="m-0 grid gap-2 p-0 text-sm text-muted-foreground">
          <li>{labels.connectorDataNoticeScope}</li>
          <li>{labels.connectorDataNoticeStorage}</li>
          <li>{labels.connectorDataNoticeAi}</li>
        </ul>
        <div className="flex items-start gap-2 pt-1">
          <Checkbox
            id="connector-authorization-confirmation"
            checked={confirmed}
            onCheckedChange={(value) => setConfirmed(value === true)}
          />
          <Label
            htmlFor="connector-authorization-confirmation"
            className="items-start whitespace-normal text-sm leading-5"
          >
            {labels.connectorDataNoticeConfirm}
          </Label>
        </div>
        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline">{labels.cancel}</Button>
          </DialogClose>
          <Button
            type="button"
            disabled={!connector || !confirmed || busy}
            onClick={async () => {
              if (!connector) return
              await onConfirm(connector)
              setConfirmed(false)
            }}
          >
            {busy ? <Spinner data-icon="inline-start" aria-hidden="true" /> : null}
            {labels.continueToConnector}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
