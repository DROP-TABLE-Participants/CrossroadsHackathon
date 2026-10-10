import { WarningCircle } from '@phosphor-icons/react'

import type { AdminLabels } from '@/components/admin/labels'
import { Button } from '@/components/ui/button'
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Spinner } from '@/components/ui/spinner'

export function AdminLoading({ labels }: { labels: AdminLabels }) {
  return (
    <div className="flex min-h-48 items-center justify-center gap-2 text-sm text-muted-foreground">
      <Spinner className="size-4" />
      {labels.loading}
    </div>
  )
}

export function AdminError({
  message,
  labels,
  retry,
}: {
  message: string
  labels: AdminLabels
  retry: () => void
}) {
  return (
    <Empty className="min-h-56 border">
      <EmptyHeader>
        <EmptyMedia variant="icon">
          <WarningCircle />
        </EmptyMedia>
        <EmptyTitle>{message}</EmptyTitle>
        <EmptyDescription>{labels.retry}</EmptyDescription>
      </EmptyHeader>
      <Button variant="outline" onClick={retry}>{labels.retry}</Button>
    </Empty>
  )
}

export function AdminEmpty({ labels }: { labels: AdminLabels }) {
  return (
    <Empty className="min-h-48 border">
      <EmptyHeader>
        <EmptyTitle>{labels.noResults}</EmptyTitle>
      </EmptyHeader>
    </Empty>
  )
}
