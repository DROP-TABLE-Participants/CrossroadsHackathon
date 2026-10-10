'use client'

import { MagnifyingGlass } from '@phosphor-icons/react'
import Link from 'next/link'
import { useSearchParams } from 'next/navigation'
import { useCallback, useEffect, useState } from 'react'

import { adminApi } from '@/components/admin/api'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPagination } from '@/components/admin/AdminPagination'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminEmpty, AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import { AdminStatusBadge } from '@/components/admin/AdminStatusBadge'
import type { AdminPaged, AdminProject } from '@/components/admin/types'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import type { Locale } from '@/i18n/config'

const statuses = [
  'draft',
  'gathering_information',
  'waiting_for_evidence',
  'needs_information',
  'generating',
  'generated',
  'failed',
]

export function AdminProjectsPage({ locale, labels }: { locale: Locale; labels: AdminLabels }) {
  const searchParams = useSearchParams()
  const [query, setQuery] = useState('')
  const [submittedQuery, setSubmittedQuery] = useState('')
  const [status, setStatus] = useState(searchParams.get('status') ?? 'all')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<AdminPaged<{ projects: AdminProject[] }> | null>(null)
  const [error, setError] = useState('')
  const load = useCallback(async () => {
    setError('')
    const params = new URLSearchParams({ page: String(page), pageSize: '20' })
    if (submittedQuery) params.set('search', submittedQuery)
    if (status !== 'all') params.set('status', status)
    try {
      setData(await adminApi(`/api/admin/generation-projects?${params}`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load projects.')
    }
  }, [page, status, submittedQuery])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  return (
    <>
      <AdminPageHeader title={labels.projects} description={labels.projectsDescription} />
      <form
        className="flex flex-wrap gap-2"
        onSubmit={(event) => {
          event.preventDefault()
          setPage(1)
          setSubmittedQuery(query.trim())
        }}
      >
        <div className="relative min-w-64 flex-1">
          <MagnifyingGlass className="absolute top-2.5 left-3 size-4 text-muted-foreground" />
          <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={`${labels.search}…`} className="pl-9" />
        </div>
        <Select value={status} onValueChange={(value) => { setStatus(value); setPage(1) }}>
          <SelectTrigger className="w-52"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{labels.allStatuses}</SelectItem>
            {statuses.map((value) => <SelectItem key={value} value={value}>{value.replaceAll('_', ' ')}</SelectItem>)}
          </SelectContent>
        </Select>
        <Button type="submit" variant="outline">{labels.search}</Button>
      </form>
      {error ? <AdminError message={error} labels={labels} retry={() => void load()} /> : null}
      {!error && !data ? <AdminLoading labels={labels} /> : null}
      {!error && data?.projects.length === 0 ? <AdminEmpty labels={labels} /> : null}
      {data && data.projects.length > 0 ? (
        <>
          <Card>
            <CardContent className="divide-y p-0">
              {data.projects.map((project) => (
                <Link
                  href={`/${locale}/admin/projects/${project.id}`}
                  key={project.id}
                  className="flex flex-wrap items-center justify-between gap-4 p-4 hover:bg-muted/40"
                >
                  <div className="min-w-0">
                    <p className="font-medium">{project.companyName}</p>
                    <p className="truncate text-sm text-muted-foreground">
                      {project.customerName || project.customerEmail} · {project.discoveryMode.replaceAll('_', ' ')}
                    </p>
                  </div>
                  <div className="flex flex-wrap items-center gap-2">
                    {project.pendingWorkCount > 0 ? (
                      <span className="text-sm text-amber-700 dark:text-amber-300">
                        {project.pendingWorkCount} {labels.pendingWork.toLowerCase()}
                      </span>
                    ) : null}
                    {project.latestRunStatus ? <AdminStatusBadge status={project.latestRunStatus} /> : null}
                    <AdminStatusBadge status={project.status} />
                  </div>
                </Link>
              ))}
            </CardContent>
          </Card>
          <AdminPagination
            page={data.page}
            pageSize={data.pageSize}
            totalCount={data.totalCount}
            labels={labels}
            onPage={setPage}
          />
        </>
      ) : null}
    </>
  )
}
