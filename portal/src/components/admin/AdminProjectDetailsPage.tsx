'use client'

import { ArrowLeft, ArrowSquareOut } from '@phosphor-icons/react'
import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'

import { adminApi } from '@/components/admin/api'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import { AdminStatusBadge } from '@/components/admin/AdminStatusBadge'
import type { AdminProjectDetails } from '@/components/admin/types'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import type { Locale } from '@/i18n/config'

export function AdminProjectDetailsPage({
  locale,
  projectId,
  labels,
}: {
  locale: Locale
  projectId: string
  labels: AdminLabels
}) {
  const [project, setProject] = useState<AdminProjectDetails | null>(null)
  const [error, setError] = useState('')
  const load = useCallback(async () => {
    setError('')
    try {
      setProject(await adminApi(`/api/admin/generation-projects/${projectId}/diagnostics`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load project diagnostics.')
    }
  }, [projectId])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  if (error) return <AdminError message={error} labels={labels} retry={() => void load()} />
  if (!project) return <AdminLoading labels={labels} />

  return (
    <>
      <Link href={`/${locale}/admin/projects`} className="flex w-fit items-center gap-2 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft /> {labels.projects}
      </Link>
      <AdminPageHeader
        title={project.companyName}
        description={`${project.customerName || project.customerEmail} · ${project.discoveryMode.replaceAll('_', ' ')}`}
        action={<AdminStatusBadge status={project.status} />}
      />
      <section className="grid gap-4 xl:grid-cols-3">
        <Card className="xl:col-span-2">
          <CardHeader><CardTitle>{labels.discovery}</CardTitle></CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-2">
            <div className="sm:col-span-2"><p className="text-xs text-muted-foreground">{labels.goal}</p><p className="whitespace-pre-wrap">{project.goal}</p></div>
            <div><p className="text-xs text-muted-foreground">{labels.customer}</p><Link className="hover:underline" href={`/${locale}/admin/customers/${project.customerId}`}>{project.customerEmail}</Link></div>
            <div><p className="text-xs text-muted-foreground">{labels.status}</p><p>{project.status.replaceAll('_', ' ')}</p></div>
            <div><p className="text-xs text-muted-foreground">Industry</p><p>{project.industry || '—'}</p></div>
            <div><p className="text-xs text-muted-foreground">Company type</p><p>{project.companyType || '—'}</p></div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle>{labels.latestRun}</CardTitle></CardHeader>
          <CardContent className="grid gap-3 text-sm">
            <div className="flex justify-between gap-3"><span className="text-muted-foreground">{labels.status}</span><span>{project.latestRunStatus?.replaceAll('_', ' ') || 'not started'}</span></div>
            <div className="flex justify-between gap-3"><span className="text-muted-foreground">Claims</span><span>{project.latestRunClaimCount}</span></div>
            <div className="flex justify-between gap-3"><span className="text-muted-foreground">Snapshot</span><span>{project.evidenceSnapshotSequence ?? '—'}</span></div>
            <div className="flex justify-between gap-3"><span className="text-muted-foreground">Snapshot items</span><span>{project.evidenceSnapshotItemCount}</span></div>
            {project.failureCode ? <div className="rounded-md bg-destructive/10 p-3 text-destructive"><strong>{project.failureCode}</strong><p className="mt-1">{project.failureMessage}</p></div> : null}
            {project.latestRunId ? (
              <Link
                className="inline-flex items-center gap-1 text-primary hover:underline"
                href={`/${locale}/admin/orchestration/runs/${project.latestRunId}`}
              >
                {labels.runDetails} <ArrowSquareOut />
              </Link>
            ) : null}
          </CardContent>
        </Card>
      </section>
      <section className="grid gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader><CardTitle>{labels.inputSources} ({project.inputSourceCount})</CardTitle></CardHeader>
          <CardContent className="grid gap-2">
            {project.inputSources.length ? project.inputSources.map((source) => (
              <div key={source.id} className="rounded-lg border p-3">
                <div className="flex items-center justify-between gap-3">
                  <p className="font-medium">{source.providerConfigKey || source.sourceKind}</p>
                  <AdminStatusBadge status={source.status} />
                </div>
                <p className="mt-1 text-sm text-muted-foreground">
                  {source.recordCount} records · {source.textRecordCount} text · {source.scopeMode.replaceAll('_', ' ')}
                </p>
                {source.lastError ? <p className="mt-2 text-sm text-destructive">{source.lastError}</p> : null}
                {source.lastWarning ? <p className="mt-2 text-sm text-amber-700 dark:text-amber-300">{source.lastWarning}</p> : null}
              </div>
            )) : <p className="text-sm text-muted-foreground">{labels.noSources}</p>}
            <p className="text-xs text-muted-foreground">{project.evidenceDocumentCount} manual evidence documents</p>
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle>{labels.interviews}</CardTitle></CardHeader>
          <CardContent className="grid gap-4">
            <div className="grid grid-cols-2 gap-3">
              <div className="rounded-lg border p-3"><p className="text-2xl font-semibold">{project.interviewCount}</p><p className="text-sm text-muted-foreground">{labels.interviews}</p></div>
              <div className="rounded-lg border p-3"><p className="text-2xl font-semibold">{project.answeredInterviewCount}</p><p className="text-sm text-muted-foreground">Answered</p></div>
            </div>
            <div className="grid gap-2">
              <p className="font-medium">{labels.pendingWork}</p>
              {project.pendingWork.length ? project.pendingWork.map((item) => (
                <div key={item.id} className="rounded-lg border p-3">
                  <div className="flex items-center gap-2">
                    <AdminStatusBadge status={item.status} />
                    {item.blocking ? <Badge variant="outline">blocking</Badge> : null}
                  </div>
                  <p className="mt-2 text-sm">{item.prompt}</p>
                  {item.targetRole ? <p className="mt-1 text-xs text-muted-foreground">{item.targetRole}</p> : null}
                </div>
              )) : <p className="text-sm text-muted-foreground">{labels.noPendingWork}</p>}
            </div>
          </CardContent>
        </Card>
      </section>
      <Card>
        <CardHeader><CardTitle>{labels.deployments}</CardTitle></CardHeader>
        <CardContent className="grid gap-2">
          {project.deployments.length ? project.deployments.map((deployment) => (
            <div key={deployment.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border p-3">
              <div>
                <p className="font-medium">{deployment.environment}</p>
                <p className="text-sm text-muted-foreground">{labels.tests}: {deployment.testsStatus || '—'} · Attempts: {deployment.attemptCount}</p>
                {deployment.failureMessage ? <p className="mt-1 text-sm text-destructive">{deployment.failureCode}: {deployment.failureMessage}</p> : null}
              </div>
              <div className="flex items-center gap-2">
                <AdminStatusBadge status={deployment.status} />
                {deployment.runtimeUrl ? (
                  <a className="inline-flex items-center gap-1 text-sm text-primary hover:underline" href={deployment.runtimeUrl} target="_blank" rel="noreferrer">
                    {labels.runtime} <ArrowSquareOut />
                  </a>
                ) : null}
              </div>
            </div>
          )) : <p className="text-sm text-muted-foreground">{labels.noDeployments}</p>}
        </CardContent>
      </Card>
      <Card>
        <CardHeader><CardTitle>{labels.recentActivity}</CardTitle></CardHeader>
        <CardContent className="grid gap-2">
          {project.recentAuditRecords.length ? project.recentAuditRecords.map((record) => (
            <div key={record.id} className="rounded-lg border p-3">
              <p className="font-medium">{record.action.replaceAll('_', ' ')}</p>
              <p className="text-sm text-muted-foreground">{record.actorEmail} · {new Date(record.createdAt).toLocaleString(locale)}</p>
            </div>
          )) : <p className="text-sm text-muted-foreground">{labels.noResults}</p>}
        </CardContent>
      </Card>
    </>
  )
}
