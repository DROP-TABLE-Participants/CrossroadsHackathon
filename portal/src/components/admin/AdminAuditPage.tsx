'use client'

import { MagnifyingGlass } from '@phosphor-icons/react'
import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'

import { adminApi } from '@/components/admin/api'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPagination } from '@/components/admin/AdminPagination'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminEmpty, AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import type { AdminAuditRecord, AdminPaged } from '@/components/admin/types'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import type { Locale } from '@/i18n/config'

function safeDetails(value: string) {
  try {
    const parsed = JSON.parse(value)
    return JSON.stringify(parsed, null, 2)
  } catch {
    return '{}'
  }
}

export function AdminAuditPage({ locale, labels }: { locale: Locale; labels: AdminLabels }) {
  const [query, setQuery] = useState('')
  const [action, setAction] = useState('')
  const [resourceType, setResourceType] = useState('')
  const [dateFrom, setDateFrom] = useState('')
  const [dateTo, setDateTo] = useState('')
  const [filters, setFilters] = useState({
    query: '',
    action: '',
    resourceType: '',
    dateFrom: '',
    dateTo: '',
  })
  const [page, setPage] = useState(1)
  const [data, setData] = useState<AdminPaged<{ records: AdminAuditRecord[] }> | null>(null)
  const [error, setError] = useState('')
  const load = useCallback(async () => {
    setError('')
    const params = new URLSearchParams({ page: String(page), pageSize: '30' })
    if (filters.query) params.set('search', filters.query)
    if (filters.action) params.set('action', filters.action)
    if (filters.resourceType) params.set('resourceType', filters.resourceType)
    if (filters.dateFrom) params.set('from', new Date(`${filters.dateFrom}T00:00:00.000Z`).toISOString())
    if (filters.dateTo) params.set('to', new Date(`${filters.dateTo}T23:59:59.999Z`).toISOString())
    try {
      setData(await adminApi(`/api/admin/audit?${params}`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load the audit log.')
    }
  }, [filters, page])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  return (
    <>
      <AdminPageHeader title={labels.auditLog} description={labels.auditDescription} />
      <form
        className="grid gap-2 md:grid-cols-2 xl:grid-cols-[1fr_220px_180px_160px_160px_auto]"
        onSubmit={(event) => {
          event.preventDefault()
          setPage(1)
          setFilters({
            query: query.trim(),
            action: action.trim(),
            resourceType: resourceType.trim(),
            dateFrom,
            dateTo,
          })
        }}
      >
        <div className="relative">
          <MagnifyingGlass className="absolute top-2.5 left-3 size-4 text-muted-foreground" />
          <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={`${labels.search} ${labels.actor.toLowerCase()}…`} className="pl-9" />
        </div>
        <Input value={action} onChange={(event) => setAction(event.target.value)} placeholder={labels.action} />
        <Input value={resourceType} onChange={(event) => setResourceType(event.target.value)} placeholder={labels.resource} />
        <Input aria-label={labels.dateFrom} type="date" value={dateFrom} onChange={(event) => setDateFrom(event.target.value)} />
        <Input aria-label={labels.dateTo} type="date" value={dateTo} onChange={(event) => setDateTo(event.target.value)} />
        <Button type="submit" variant="outline">{labels.search}</Button>
      </form>
      {error ? <AdminError message={error} labels={labels} retry={() => void load()} /> : null}
      {!error && !data ? <AdminLoading labels={labels} /> : null}
      {!error && data?.records.length === 0 ? <AdminEmpty labels={labels} /> : null}
      {data && data.records.length > 0 ? (
        <>
          <Card>
            <CardContent className="divide-y p-0">
              {data.records.map((record) => (
                <details key={record.id} className="group p-4">
                  <summary className="flex cursor-pointer list-none flex-wrap items-start justify-between gap-3">
                    <div>
                      <p className="font-medium">{record.action.replaceAll('_', ' ')}</p>
                      <p className="text-sm text-muted-foreground">
                        {record.actorEmail} · {record.resourceType} ·{' '}
                        {record.resourceType === 'customer' ? (
                          <Link className="hover:underline" href={`/${locale}/admin/customers/${record.resourceId}`}>
                            {record.resourceId}
                          </Link>
                        ) : record.resourceType === 'generation_project' ? (
                          <Link className="hover:underline" href={`/${locale}/admin/projects/${record.resourceId}`}>
                            {record.resourceId}
                          </Link>
                        ) : record.resourceId}
                      </p>
                    </div>
                    <time className="text-xs text-muted-foreground">{new Date(record.createdAt).toLocaleString(locale)}</time>
                  </summary>
                  <pre className="mt-3 overflow-x-auto rounded-md bg-muted p-3 text-xs">{safeDetails(record.safeDetailsJson)}</pre>
                </details>
              ))}
            </CardContent>
          </Card>
          <AdminPagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} labels={labels} onPage={setPage} />
        </>
      ) : null}
    </>
  )
}
