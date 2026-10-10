'use client'

import type { AdminLabels } from '@/components/admin/labels'
import { Button } from '@/components/ui/button'

export function AdminPagination({
  page,
  pageSize,
  totalCount,
  labels,
  onPage,
}: {
  page: number
  pageSize: number
  totalCount: number
  labels: AdminLabels
  onPage: (page: number) => void
}) {
  const pageCount = Math.max(1, Math.ceil(totalCount / pageSize))
  return (
    <div className="flex items-center justify-between gap-3">
      <span className="text-sm text-muted-foreground">
        {labels.page} {page} {labels.of} {pageCount}
      </span>
      <div className="flex gap-2">
        <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
          {labels.previous}
        </Button>
        <Button
          variant="outline"
          size="sm"
          disabled={page >= pageCount}
          onClick={() => onPage(page + 1)}
        >
          {labels.next}
        </Button>
      </div>
    </div>
  )
}
