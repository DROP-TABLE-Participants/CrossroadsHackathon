'use client'

import {
  ArrowLeft,
  ArrowSquareOut,
  ArrowClockwise,
  Play,
  XCircle,
} from '@phosphor-icons/react'
import Link from 'next/link'
import { useRouter } from 'next/navigation'
import { useCallback, useEffect, useState } from 'react'
import { toast } from 'sonner'

import { adminApi } from '@/components/admin/api'
import { useAdminSession } from '@/components/admin/AdminSessionProvider'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import { AdminStatusBadge } from '@/components/admin/AdminStatusBadge'
import { AdminRunControlDialog } from '@/components/admin/AdminRunControlDialog'
import { formatOrchestrationCode } from '@/components/admin/formatOrchestrationCode'
import { useAdminOrchestrationRealtime } from '@/components/admin/hooks/useAdminOrchestrationRealtime'
import type { AdminGenerationRunDetails } from '@/components/admin/types'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import type { Locale } from '@/i18n/config'

function date(value: string | undefined, locale: Locale) {
  return value ? new Date(value).toLocaleString(locale) : '—'
}

export function AdminGenerationRunDetailsPage({
  locale,
  runId,
  labels,
}: {
  locale: Locale
  runId: string
  labels: AdminLabels
}) {
  const router = useRouter()
  const { sessionResolved } = useAdminSession()
  const [run, setRun] = useState<AdminGenerationRunDetails | null>(null)
  const [error, setError] = useState('')
  const [activeAction, setActiveAction] = useState<string | null>(null)
  const handleSessionExpired = useCallback(() => {
    router.replace(`/${locale}/admin/login`)
  }, [locale, router])

  const load = useCallback(async () => {
    setError('')
    try {
      setRun(await adminApi<AdminGenerationRunDetails>(
        `/api/admin/orchestration/runs/${runId}`,
      ))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load the generation run.')
    }
  }, [runId])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])
  const realtimeRefresh = useCallback(async (projectId?: string) => {
    if (!projectId || !run || projectId === run.generationProjectId) await load()
  }, [load, run])
  useAdminOrchestrationRealtime({
    enabled: sessionResolved,
    onChanged: realtimeRefresh,
    onSessionExpired: handleSessionExpired,
  })

  const control = async (
    action: 'retry' | 'cancel' | 'resume',
    reason: string,
  ) => {
    setActiveAction(action)
    try {
      await adminApi(`/api/admin/orchestration/runs/${runId}/${action}`, {
        method: 'POST',
        body: JSON.stringify({ reason }),
      })
      toast.success(
        action === 'retry'
          ? labels.retryScheduled
          : action === 'cancel'
            ? labels.cancellationRequested
            : labels.runResumed,
      )
      await load()
    } catch (reason) {
      toast.error(
        reason instanceof Error
          ? reason.message
          : 'The generation control action could not be completed.',
      )
    } finally {
      setActiveAction(null)
    }
  }

  if (error) return <AdminError message={error} labels={labels} retry={() => void load()} />
  if (!run) return <AdminLoading labels={labels} />

  return (
    <>
      <Link
        href={`/${locale}/admin/orchestration`}
        className="flex w-fit items-center gap-2 text-sm text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft /> {labels.orchestration}
      </Link>
      <AdminPageHeader
        title={run.companyName}
        description={`${labels.runDetails} · ${run.customerEmail}`}
        action={(
          <div className="flex items-center gap-2">
            <AdminStatusBadge
              status={run.status}
              label={formatOrchestrationCode(run.status, locale)}
            />
            {run.canRetryNow ? (
              <AdminRunControlDialog
                triggerLabel={labels.retryNow}
                title={labels.retryNowTitle}
                description={labels.retryNowDescription}
                reasonLabel={labels.actionReason}
                reasonPlaceholder={labels.actionReasonPlaceholder}
                cancelLabel={labels.cancel}
                confirmLabel={labels.retryNow}
                icon={<ArrowClockwise />}
                loading={activeAction !== null}
                onConfirm={(reason) => control('retry', reason)}
              />
            ) : null}
            {run.canCancel ? (
              <AdminRunControlDialog
                triggerLabel={labels.cancelRun}
                title={labels.cancelRunTitle}
                description={labels.cancelRunDescription}
                reasonLabel={labels.actionReason}
                reasonPlaceholder={labels.actionReasonPlaceholder}
                cancelLabel={labels.cancel}
                confirmLabel={labels.cancelRun}
                icon={<XCircle />}
                loading={activeAction !== null}
                destructive
                onConfirm={(reason) => control('cancel', reason)}
              />
            ) : null}
            {run.canResume ? (
              <AdminRunControlDialog
                triggerLabel={labels.resumeRun}
                title={labels.resumeRunTitle}
                description={labels.resumeRunDescription}
                reasonLabel={labels.actionReason}
                reasonPlaceholder={labels.actionReasonPlaceholder}
                cancelLabel={labels.cancel}
                confirmLabel={labels.resumeRun}
                icon={<Play />}
                loading={activeAction !== null}
                onConfirm={(reason) => control('resume', reason)}
              />
            ) : null}
          </div>
        )}
      />
      <section className="grid gap-4 xl:grid-cols-3">
        <Card className="xl:col-span-2">
          <CardHeader><CardTitle>{labels.diagnostics}</CardTitle></CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-2">
            <div>
              <p className="text-xs text-muted-foreground">{labels.requestedMode}</p>
              <p>{formatOrchestrationCode(run.requestedMode, locale)}</p>
            </div>
            <div>
              <p className="text-xs text-muted-foreground">{labels.actualMode}</p>
              <p>{run.actualMode ? formatOrchestrationCode(run.actualMode, locale) : '—'}</p>
            </div>
            <div>
              <p className="text-xs text-muted-foreground">{labels.claims}</p>
              <p>{run.claimCount}</p>
            </div>
            <div>
              <p className="text-xs text-muted-foreground">{labels.failures}</p>
              <p>{run.failureCount}</p>
            </div>
            <div>
              <p className="text-xs text-muted-foreground">{labels.heartbeat}</p>
              <p>{date(run.heartbeatAt, locale)}</p>
            </div>
            <div>
              <p className="text-xs text-muted-foreground">{labels.lease}</p>
              <p>{run.leaseOwner || '—'} · {date(run.leaseExpiresAt, locale)}</p>
            </div>
            <div>
              <p className="text-xs text-muted-foreground">{labels.created}</p>
              <p>{date(run.createdAt, locale)}</p>
            </div>
            <div>
              <p className="text-xs text-muted-foreground">{labels.updated}</p>
              <p>{date(run.updatedAt, locale)}</p>
            </div>
            {run.cancellationRequestedAt ? (
              <div>
                <p className="text-xs text-muted-foreground">
                  {labels.cancellationRequestedRuns}
                </p>
                <p>{date(run.cancellationRequestedAt, locale)}</p>
              </div>
            ) : null}
            {run.cancelledAt ? (
              <div>
                <p className="text-xs text-muted-foreground">
                  {formatOrchestrationCode('cancelled', locale)}
                </p>
                <p>{date(run.cancelledAt, locale)}</p>
              </div>
            ) : null}
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle>{labels.evidenceSnapshot}</CardTitle></CardHeader>
          <CardContent className="grid gap-3 text-sm">
            <div className="flex justify-between gap-3">
              <span className="text-muted-foreground">Sequence</span>
              <span>{run.evidenceSnapshotSequence ?? '—'}</span>
            </div>
            <div className="flex justify-between gap-3">
              <span className="text-muted-foreground">Items</span>
              <span>{run.evidenceSnapshotItemCount}</span>
            </div>
            <p className="break-all font-mono text-xs text-muted-foreground">
              {run.evidenceSnapshotHash || '—'}
            </p>
          </CardContent>
        </Card>
      </section>
      {run.failureCode ? (
        <Card className="border-destructive/40">
          <CardHeader><CardTitle className="text-destructive">{labels.failure}</CardTitle></CardHeader>
          <CardContent>
            <div className="flex flex-wrap items-center gap-2">
              <AdminStatusBadge
                status={run.failureDisposition || 'failed'}
                label={formatOrchestrationCode(
                  run.failureDisposition || 'failed',
                  locale,
                )}
              />
              <strong>{run.failureCode}</strong>
            </div>
            <p className="mt-2 text-sm">{run.failureMessage}</p>
            {run.nextAttemptAt ? (
              <p className="mt-2 text-sm text-muted-foreground">
                {labels.retryScheduled}: {date(run.nextAttemptAt, locale)}
              </p>
            ) : null}
          </CardContent>
        </Card>
      ) : null}
      <section className="grid gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader><CardTitle>{labels.stages}</CardTitle></CardHeader>
          <CardContent className="grid gap-3">
            {run.stages.map((stage) => (
              <div key={stage.id} className="rounded-lg border p-3">
                <div className="flex items-center justify-between gap-3">
                  <p className="font-medium">
                    {formatOrchestrationCode(stage.stageKey, locale)}
                  </p>
                  <AdminStatusBadge
                    status={stage.status}
                    label={formatOrchestrationCode(stage.status, locale)}
                  />
                </div>
                <p className="mt-1 text-xs text-muted-foreground">
                  {labels.attempts}: {stage.attemptCount} · {date(stage.startedAt, locale)}
                </p>
                {stage.summary ? <p className="mt-2 text-sm">{stage.summary}</p> : null}
                {stage.failureCode ? (
                  <p className="mt-2 text-sm text-destructive">
                    {stage.failureCode}: {stage.failureMessage}
                  </p>
                ) : null}
                {stage.inputHash ? (
                  <p className="mt-2 truncate font-mono text-xs text-muted-foreground" title={stage.inputHash}>
                    {stage.inputHash}
                  </p>
                ) : null}
              </div>
            ))}
            {!run.stages.length ? <p className="text-sm text-muted-foreground">{labels.noStages}</p> : null}
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle>{labels.timeline}</CardTitle></CardHeader>
          <CardContent className="grid gap-3">
            {run.events.map((event) => (
              <div key={event.id} className="relative border-l pl-4">
                <span className="absolute top-1 -left-1 size-2 rounded-full bg-primary" />
                <div className="flex flex-wrap items-center gap-2">
                  <p className="font-medium">
                    {formatOrchestrationCode(event.eventType, locale)}
                  </p>
                  {event.stageKey ? (
                    <AdminStatusBadge
                      status={event.stageKey}
                      label={formatOrchestrationCode(event.stageKey, locale)}
                    />
                  ) : null}
                </div>
                <p className="mt-1 text-sm">{event.summary}</p>
                <time className="text-xs text-muted-foreground">
                  {date(event.createdAt, locale)}
                </time>
              </div>
            ))}
            {!run.events.length ? <p className="text-sm text-muted-foreground">{labels.noEvents}</p> : null}
          </CardContent>
        </Card>
      </section>
      <section className="grid gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader><CardTitle>{labels.agentWorkItems}</CardTitle></CardHeader>
          <CardContent className="grid gap-3">
            {run.agentWorkItems.map((item) => (
              <div key={item.id} className="rounded-lg border p-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div>
                    <p className="font-medium">
                      {formatOrchestrationCode(item.agentKey, locale)}
                    </p>
                    <p className="text-xs text-muted-foreground">
                      {item.agentLayer} · v{item.agentVersion} · contract {item.contractVersion}
                    </p>
                  </div>
                  <AdminStatusBadge
                    status={item.status}
                    label={formatOrchestrationCode(item.status, locale)}
                  />
                </div>
                <p className="mt-2 text-xs text-muted-foreground">
                  {labels.attempts}: {item.attemptCount} · repairs: {item.repairCount} · routes: {item.routingDepth}
                </p>
                {item.summary ? <p className="mt-2 text-sm">{item.summary}</p> : null}
                {item.failureCode ? (
                  <p className="mt-2 text-sm text-destructive">
                    {item.failureCode}: {item.failureMessage}
                  </p>
                ) : null}
                <p
                  className="mt-2 truncate font-mono text-xs text-muted-foreground"
                  title={item.inputManifestHash}
                >
                  input {item.inputManifestHash}
                </p>
                {item.attempts.length ? (
                  <div className="mt-3 grid gap-2 border-t pt-3">
                    {item.attempts.map((attempt) => (
                      <div
                        key={attempt.id}
                        className="flex flex-wrap items-start justify-between gap-2 text-xs"
                      >
                        <div>
                          <p className="font-medium">
                            {labels.attempts} {attempt.attemptNumber}
                            {attempt.modelCallCount
                              ? ` · ${attempt.modelCallCount} ${labels.modelCalls.toLowerCase()}`
                              : ''}
                          </p>
                          {attempt.failureCode ? (
                            <p className="mt-1 text-destructive">
                              {attempt.failureCode}: {attempt.failureMessage}
                            </p>
                          ) : null}
                          {attempt.repairInstructions ? (
                            <p className="mt-1 text-muted-foreground">
                              {attempt.repairInstructions}
                            </p>
                          ) : null}
                        </div>
                        <AdminStatusBadge
                          status={attempt.status}
                          label={formatOrchestrationCode(attempt.status, locale)}
                        />
                      </div>
                    ))}
                  </div>
                ) : null}
              </div>
            ))}
            {!run.agentWorkItems.length ? (
              <p className="text-sm text-muted-foreground">{labels.noAgentWorkItems}</p>
            ) : null}
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle>{labels.artifacts}</CardTitle></CardHeader>
          <CardContent className="grid gap-3">
            {run.artifacts.map((artifact) => (
              <div key={artifact.id} className="rounded-lg border p-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <p className="font-medium">
                    {formatOrchestrationCode(artifact.artifactType, locale)} v{artifact.version}
                  </p>
                  <AdminStatusBadge
                    status={artifact.validationStatus}
                    label={formatOrchestrationCode(artifact.validationStatus, locale)}
                  />
                </div>
                <p className="mt-1 text-xs text-muted-foreground">
                  {artifact.schemaName} · schema {artifact.schemaVersion} · {artifact.producerAgentKey} v{artifact.producerAgentVersion}
                </p>
                {artifact.validationSummary ? (
                  <p className="mt-2 text-sm">{artifact.validationSummary}</p>
                ) : null}
                <p
                  className="mt-2 truncate font-mono text-xs text-muted-foreground"
                  title={artifact.contentHash}
                >
                  {artifact.contentHash}
                </p>
              </div>
            ))}
            {!run.artifacts.length ? (
              <p className="text-sm text-muted-foreground">{labels.noArtifacts}</p>
            ) : null}
          </CardContent>
        </Card>
      </section>
      <Card>
        <CardHeader><CardTitle>{labels.modelCalls}</CardTitle></CardHeader>
        <CardContent className="grid gap-2">
          {run.modelCalls.map((call) => (
            <div key={call.id} className="grid gap-2 rounded-lg border p-3 md:grid-cols-[1fr_auto]">
              <div className="min-w-0">
                <p className="font-medium">
                  {formatOrchestrationCode(call.phase, locale)}
                </p>
                {call.agentKey ? (
                  <p className="text-xs text-muted-foreground">
                    {formatOrchestrationCode(call.agentKey, locale)} v{call.agentVersion} · {call.model}
                    {call.durationMilliseconds !== undefined
                      ? ` · ${call.durationMilliseconds} ms`
                      : ''}
                  </p>
                ) : null}
                <p className="truncate font-mono text-xs text-muted-foreground" title={call.requestHash}>
                  {call.requestHash}
                </p>
                {call.providerResponseId ? (
                  <p className="mt-1 truncate text-xs text-muted-foreground">
                    {labels.providerResponse}: {call.providerResponseId}
                  </p>
                ) : null}
              </div>
              <div className="flex items-center gap-2">
                <AdminStatusBadge
                  status={call.status}
                  label={formatOrchestrationCode(call.status, locale)}
                />
                <time className="text-xs text-muted-foreground">{date(call.updatedAt, locale)}</time>
              </div>
            </div>
          ))}
          {!run.modelCalls.length ? (
            <p className="text-sm text-muted-foreground">{labels.noModelCalls}</p>
          ) : null}
        </CardContent>
      </Card>
      <section className="grid gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader><CardTitle>{labels.pendingWork}</CardTitle></CardHeader>
          <CardContent className="grid gap-2">
            {run.pendingWork.map((item) => (
              <div key={item.id} className="rounded-lg border p-3">
                <div className="flex items-center gap-2">
                  <AdminStatusBadge
                    status={item.status}
                    label={formatOrchestrationCode(item.status, locale)}
                  />
                  <span className="text-xs text-muted-foreground">
                    {formatOrchestrationCode(item.kind, locale)}
                  </span>
                </div>
                <p className="mt-2 text-sm">{item.prompt}</p>
              </div>
            ))}
            {!run.pendingWork.length ? (
              <p className="text-sm text-muted-foreground">{labels.noPendingWork}</p>
            ) : null}
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle>{labels.deployments}</CardTitle></CardHeader>
          <CardContent className="grid gap-2">
            {run.deployments.map((deployment) => (
              <div key={deployment.id} className="flex items-center justify-between gap-3 rounded-lg border p-3">
                <div>
                  <p className="font-medium">
                    {formatOrchestrationCode(deployment.environment, locale)}
                  </p>
                  <p className="text-sm text-muted-foreground">
                    {labels.tests}: {deployment.testsStatus || '—'}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <AdminStatusBadge
                    status={deployment.status}
                    label={formatOrchestrationCode(deployment.status, locale)}
                  />
                  {deployment.runtimeUrl ? (
                    <a href={deployment.runtimeUrl} target="_blank" rel="noreferrer">
                      <ArrowSquareOut aria-label={labels.runtime} />
                    </a>
                  ) : null}
                </div>
              </div>
            ))}
            {!run.deployments.length ? (
              <p className="text-sm text-muted-foreground">{labels.noDeployments}</p>
            ) : null}
          </CardContent>
        </Card>
      </section>
      <Button variant="outline" asChild>
        <Link href={`/${locale}/admin/projects/${run.generationProjectId}`}>
          {labels.projectDetails}
        </Link>
      </Button>
    </>
  )
}
