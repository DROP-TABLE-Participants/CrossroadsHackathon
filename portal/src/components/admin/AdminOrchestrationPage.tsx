'use client'

import {
  ArrowClockwise,
  ClockCountdown,
  Hourglass,
  MagnifyingGlass,
  Pulse,
  WarningCircle,
  XCircle,
} from '@phosphor-icons/react'
import Link from 'next/link'
import { useRouter, useSearchParams } from 'next/navigation'
import { useCallback, useEffect, useState } from 'react'

import { adminApi } from '@/components/admin/api'
import { useAdminSession } from '@/components/admin/AdminSessionProvider'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPagination } from '@/components/admin/AdminPagination'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminEmpty, AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import { AdminStatusBadge } from '@/components/admin/AdminStatusBadge'
import { formatOrchestrationCode } from '@/components/admin/formatOrchestrationCode'
import { useAdminOrchestrationRealtime } from '@/components/admin/hooks/useAdminOrchestrationRealtime'
import type {
  AdminGenerationRun,
  AdminOrchestrationOverview,
  AdminPaged,
} from '@/components/admin/types'
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
  'queued',
  'running',
  'waiting_for_input',
  'cancellation_requested',
  'cancelled',
  'retry_scheduled',
  'completed',
  'failed',
]
const stages = ['company_understanding', 'erp_design']
const dispositions = ['retryable', 'terminal', 'configuration']

export function AdminOrchestrationPage({
  locale,
  labels,
}: {
  locale: Locale
  labels: AdminLabels
}) {
  const router = useRouter()
  const searchParams = useSearchParams()
  const { sessionResolved } = useAdminSession()
  const [query, setQuery] = useState('')
  const [submittedQuery, setSubmittedQuery] = useState('')
  const [status, setStatus] = useState(searchParams.get('status') ?? 'all')
  const [stage, setStage] = useState('all')
  const [disposition, setDisposition] = useState('all')
  const [page, setPage] = useState(1)
  const [overview, setOverview] = useState<AdminOrchestrationOverview | null>(null)
  const [data, setData] = useState<AdminPaged<{ runs: AdminGenerationRun[] }> | null>(null)
  const [error, setError] = useState('')
  const handleSessionExpired = useCallback(() => {
    router.replace(`/${locale}/admin/login`)
  }, [locale, router])

  const load = useCallback(async () => {
    setError('')
    const params = new URLSearchParams({ page: String(page), pageSize: '20' })
    if (submittedQuery) params.set('search', submittedQuery)
    if (status !== 'all') params.set('status', status)
    if (stage !== 'all') params.set('stage', stage)
    if (disposition !== 'all') params.set('failureDisposition', disposition)
    try {
      const [nextOverview, nextRuns] = await Promise.all([
        adminApi<AdminOrchestrationOverview>('/api/admin/orchestration/overview'),
        adminApi<AdminPaged<{ runs: AdminGenerationRun[] }>>(
          `/api/admin/orchestration/runs?${params}`,
        ),
      ])
      setOverview(nextOverview)
      setData(nextRuns)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load orchestration.')
    }
  }, [disposition, page, stage, status, submittedQuery])

  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])
  useAdminOrchestrationRealtime({
    enabled: sessionResolved,
    onChanged: load,
    onSessionExpired: handleSessionExpired,
  })

  const cards = overview
    ? [
        { label: labels.queuedRuns, value: overview.queuedRunCount, icon: Hourglass, status: 'queued' },
        { label: labels.runningRuns, value: overview.runningRunCount, icon: Pulse, status: 'running' },
        { label: labels.resumingRuns, value: overview.resumingRunCount, icon: ArrowClockwise, status: 'resuming' },
        { label: labels.resumeFailedRuns, value: overview.resumeFailedRunCount, icon: WarningCircle, status: 'resume_failed' },
        { label: labels.waitingForInput, value: overview.waitingForInputRunCount, icon: ClockCountdown, status: 'waiting_for_input' },
        { label: labels.cancellationRequestedRuns, value: overview.cancellationRequestedRunCount, icon: XCircle, status: 'cancellation_requested' },
        { label: labels.retryScheduled, value: overview.retryScheduledRunCount, icon: ArrowClockwise, status: 'retry_scheduled' },
        { label: labels.failedLast24Hours, value: overview.failedLast24HoursCount, icon: WarningCircle, status: 'failed' },
        { label: labels.staleLeases, value: overview.staleLeaseCount, icon: WarningCircle, status: 'running' },
      ]
    : []

  return (
    <>
      <AdminPageHeader
        title={labels.orchestration}
        description={labels.orchestrationDescription}
      />
      {overview ? (
        <>
          <section className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
            {cards.map((card) => (
              <button
                type="button"
                key={card.label}
                className="text-left"
                onClick={() => {
                  setStatus(card.status)
                  setPage(1)
                }}
              >
                <Card className="h-full transition-colors hover:border-primary/40 hover:bg-muted/20">
                  <CardContent className="flex items-center justify-between gap-4">
                    <div>
                      <p className="text-sm text-muted-foreground">{card.label}</p>
                      <p className="mt-1 text-3xl font-semibold">{card.value}</p>
                    </div>
                    <div className="rounded-lg bg-primary/10 p-2 text-primary">
                      <card.icon className="size-5" aria-hidden="true" />
                    </div>
                  </CardContent>
                </Card>
              </button>
            ))}
          </section>
          <p className="text-sm text-muted-foreground">
            {labels.oldestQueued}:{' '}
            {overview.oldestQueuedAt
              ? new Date(overview.oldestQueuedAt).toLocaleString(locale)
              : '—'}
          </p>
        </>
      ) : null}
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
          <Input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder={`${labels.search}…`}
            className="pl-9"
          />
        </div>
        <Select value={status} onValueChange={(value) => { setStatus(value); setPage(1) }}>
          <SelectTrigger className="w-48"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{labels.allStatuses}</SelectItem>
            {statuses.map((value) => (
              <SelectItem key={value} value={value}>
                {formatOrchestrationCode(value, locale)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={stage} onValueChange={(value) => { setStage(value); setPage(1) }}>
          <SelectTrigger className="w-52"><SelectValue placeholder={labels.currentStage} /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{labels.stages}</SelectItem>
            {stages.map((value) => (
              <SelectItem key={value} value={value}>
                {formatOrchestrationCode(value, locale)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={disposition} onValueChange={(value) => { setDisposition(value); setPage(1) }}>
          <SelectTrigger className="w-44"><SelectValue placeholder={labels.failure} /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{labels.failure}</SelectItem>
            {dispositions.map((value) => (
              <SelectItem key={value} value={value}>
                {formatOrchestrationCode(value, locale)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button type="submit" variant="outline">{labels.search}</Button>
      </form>
      {error ? <AdminError message={error} labels={labels} retry={() => void load()} /> : null}
      {!error && (!overview || !data) ? <AdminLoading labels={labels} /> : null}
      {!error && data?.runs.length === 0 ? <AdminEmpty labels={labels} /> : null}
      {data && data.runs.length > 0 ? (
        <>
          <Card>
            <CardContent className="divide-y p-0">
              {data.runs.map((run) => (
                <Link
                  href={`/${locale}/admin/orchestration/runs/${run.id}`}
                  key={run.id}
                  className="flex flex-wrap items-center justify-between gap-4 p-4 hover:bg-muted/40"
                >
                  <div className="min-w-0">
                    <p className="font-medium">{run.companyName}</p>
                    <p className="truncate text-sm text-muted-foreground">
                      {run.customerEmail} ·{' '}
                      {run.currentStage
                        ? formatOrchestrationCode(run.currentStage, locale)
                        : labels.created}
                    </p>
                    <p className="mt-1 text-xs text-muted-foreground">
                      {labels.claims}: {run.claimCount} · {labels.failures}: {run.failureCount} ·{' '}
                      {new Date(run.updatedAt).toLocaleString(locale)}
                    </p>
                  </div>
                  <div className="flex flex-wrap items-center gap-2">
                    {run.failureDisposition ? (
                      <AdminStatusBadge
                        status={run.failureDisposition}
                        label={formatOrchestrationCode(run.failureDisposition, locale)}
                      />
                    ) : null}
                    <AdminStatusBadge
                      status={
                        run.status === 'queued' &&
                        run.nextAttemptAt &&
                        new Date(run.nextAttemptAt) > new Date()
                          ? 'retry_scheduled'
                          : run.status
                      }
                      label={formatOrchestrationCode(
                        run.status === 'queued' &&
                        run.nextAttemptAt &&
                        new Date(run.nextAttemptAt) > new Date()
                          ? 'retry_scheduled'
                          : run.status,
                        locale,
                      )}
                    />
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
