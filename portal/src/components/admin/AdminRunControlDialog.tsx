'use client'

import type { ReactNode } from 'react'
import { useState } from 'react'

import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from '@/components/ui/alert-dialog'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'

export function AdminRunControlDialog({
  triggerLabel,
  title,
  description,
  reasonLabel,
  reasonPlaceholder,
  cancelLabel,
  confirmLabel,
  icon,
  loading,
  destructive = false,
  onConfirm,
}: {
  triggerLabel: string
  title: string
  description: string
  reasonLabel: string
  reasonPlaceholder: string
  cancelLabel: string
  confirmLabel: string
  icon: ReactNode
  loading: boolean
  destructive?: boolean
  onConfirm: (reason: string) => Promise<void>
}) {
  const [reason, setReason] = useState('')
  const validReason = reason.trim().length >= 3

  return (
    <AlertDialog>
      <AlertDialogTrigger asChild>
        <Button variant={destructive ? 'destructive' : 'default'} disabled={loading}>
          {icon}
          {triggerLabel}
        </Button>
      </AlertDialogTrigger>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription>{description}</AlertDialogDescription>
        </AlertDialogHeader>
        <label className="grid gap-2 text-sm font-medium">
          {reasonLabel}
          <Textarea
            value={reason}
            maxLength={1000}
            placeholder={reasonPlaceholder}
            onChange={(event) => setReason(event.target.value)}
          />
        </label>
        <AlertDialogFooter>
          <AlertDialogCancel>{cancelLabel}</AlertDialogCancel>
          <AlertDialogAction
            disabled={!validReason || loading}
            onClick={() => void onConfirm(reason.trim())}
          >
            {confirmLabel}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
