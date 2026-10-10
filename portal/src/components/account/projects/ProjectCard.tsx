'use client'

import { CaretDown, Trash as Trash2 } from '@phosphor-icons/react'
import { toast } from 'sonner'

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
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@/components/ui/card'
import { Spinner } from '@/components/ui/spinner'
import { cn } from '@/lib/utils'
import type { ProjectsApi } from '@/components/account/api-types'
import type { AccountLabels } from '@/components/account/labels'
import type { GenerationProjectSummary } from '@/components/account/types'
import type { Locale } from '@/i18n/config'
import { ConnectorsPanel } from './ConnectorsPanel'
import { generationInFlight, generationProgress, phaseLabel } from './generation-progress'
import { GenerationProgressPanel } from './GenerationProgressPanel'
import { InterviewsPanel } from './InterviewsPanel'
import { PendingWorkList } from './PendingWorkList'
import { ProjectPrimaryAction, projectPrimaryActionKind } from './ProjectPrimaryAction'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'

type ProjectCardProps = {
  labels: AccountLabels
  locale: Locale
  project: GenerationProjectSummary
  projectsApi: ProjectsApi
  highlighted?: boolean
  expanded?: boolean
  onExpandedChange?: () => void
}

export function ProjectCard({ labels, locale, project, projectsApi, highlighted, expanded, onExpandedChange }: ProjectCardProps) {
  const guest = useDemoBooking().customer?.isGuest === true
  const { pendingAction, deleteProject } = projectsApi
  const deleting = pendingAction === `delete-project:${project.id}`

  const confirmDelete = async () => {
    const ok = await deleteProject(project.id)
    if (ok) {
      toast.success(labels.erpDeleted)
    } else {
      toast.error(projectsApi.projectError || labels.deleteErp)
    }
  }

  const latestRun = project.generationRuns?.[0]
  const description = [project.industry, project.companyType].filter(Boolean).map(value => value!.replace(/_/g, ' ')).join(' · ')
  const pendingWork = latestRun?.pendingWorkItems || []
  const nonInterviewWork = pendingWork.filter((item) => item.kind !== 'interview_request')
  // Evidence must not shift under a run that has already collected it.
  const connectorsLocked = generationInFlight(latestRun)
  const compact = expanded !== undefined
  const open = expanded ?? true
  const actionKind = projectPrimaryActionKind(latestRun)
  const progress = generationProgress(labels, project, latestRun)
  const ready = actionKind === 'open-erp'
  const waiting = progress?.tone === 'waiting'
  const status = ready ? (locale === 'bg' ? 'Готово' : 'Ready')
    : latestRun?.erp?.status === 'failed' ? labels.erpPreparationFailed
    : progress?.tone === 'running' ? phaseLabel(labels, progress.phase)
    : latestRun ? labels.runStatusPlain(latestRun.status) : labels.projectStatus(project.status || 'draft')

  return (
    <Card
      id={`project-${project.id}`}
      className={cn(compact && 'gap-0 rounded-none border-0 border-b py-0 last:border-b-0', highlighted && 'bg-accent/50')}
    >
      <CardHeader className={cn(compact && 'block p-4')}>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex min-w-0 flex-col gap-1">
            {compact ? <button type="button" onClick={onExpandedChange} aria-expanded={open} aria-controls={`project-details-${project.id}`} className="flex min-w-0 items-center gap-2 rounded-sm text-left text-sm font-semibold focus-visible:outline-2 focus-visible:outline-ring"><CaretDown className={cn('size-4 shrink-0 text-muted-foreground', !open && '-rotate-90')} /><span className="break-words">{project.companyName}</span></button> : <CardTitle className="text-lg">{project.companyName}</CardTitle>}
            {description ? <CardDescription>{description}</CardDescription> : null}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant="outline" className={cn(ready && 'border-success/20 bg-success/5 text-green-700', progress?.tone === 'failed' && 'border-destructive/20 text-destructive', waiting && 'border-amber-200 bg-amber-50 text-amber-800')}>{status}</Badge>
            {compact && ((actionKind === 'go' && !project.generationBlockedReason) || ready) ? <ProjectPrimaryAction compact labels={labels} locale={locale} project={project} latestRun={latestRun} projectsApi={projectsApi} /> : compact ? <Button variant="outline" size="sm" onClick={onExpandedChange}>{waiting ? (locale === 'bg' ? 'Отговори на въпросите' : 'Answer questions') : progress?.tone === 'running' ? (locale === 'bg' ? 'Виж напредъка' : 'View progress') : (locale === 'bg' ? 'Подробности' : 'Details')}</Button> : null}
            <AlertDialog>
              <AlertDialogTrigger asChild>
                <Button
                  type="button"
                  size="icon-sm"
                  variant="ghost"
                  className="text-muted-foreground hover:text-destructive"
                  aria-label={labels.deleteErp}
                  title={labels.deleteErp}
                  disabled={deleting}
                >
                  {deleting ? <Spinner /> : <Trash2 />}
                </Button>
              </AlertDialogTrigger>
              <AlertDialogContent>
                <AlertDialogHeader>
                  <AlertDialogTitle>{labels.deleteErpConfirmTitle}</AlertDialogTitle>
                  <AlertDialogDescription>{labels.deleteErpConfirmDescription}</AlertDialogDescription>
                </AlertDialogHeader>
                <AlertDialogFooter>
                  <AlertDialogCancel>{labels.cancel}</AlertDialogCancel>
                  <AlertDialogAction
                    onClick={confirmDelete}
                    className="bg-destructive text-white hover:bg-destructive/90"
                  >
                    {labels.deleteErp}
                  </AlertDialogAction>
                </AlertDialogFooter>
              </AlertDialogContent>
            </AlertDialog>
          </div>
        </div>
        {/* `discoveryMode` ("connector first") is an engine routing detail and
            means nothing to a company owner — deliberately not shown. */}
        {!compact && <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
          <span>{labels.participants}: {project.participants?.length || 0}</span>
          <span>{labels.interviews}: {project.requests?.length || 0}</span>
          <span>{labels.connectedSources}: {project.inputSourceCount || 0}</span>
        </div>}
      </CardHeader>
      {open && <CardContent id={`project-details-${project.id}`} className={cn('flex flex-col gap-4', compact && 'border-t bg-background p-4')}>
        {/* "Where things stand" leads the card: the first thing an owner sees is
            what ERP Generator is doing and how far along it is. */}
        {!ready && <GenerationProgressPanel labels={labels} project={project} run={latestRun} />}

        {(!compact || (!ready && actionKind !== 'go') || project.generationBlockedReason) && <ProjectPrimaryAction
          labels={labels}
          locale={locale}
          project={project}
          latestRun={latestRun}
          projectsApi={projectsApi}
        />}

        {/* Interview work lives in the Interviews section below, which shows the
            question, who was asked and whether they answered. Leaving it here
            too meant two boxes about the same interview. */}
        {(guest ? pendingWork : nonInterviewWork).length ? (
          <PendingWorkList
            labels={labels}
            project={project}
            items={guest ? pendingWork : nonInterviewWork}
            inlineOnly={guest}
            projectsApi={projectsApi}
          />
        ) : null}

        {/* When "Where things stand" says it is waiting on an answer, the
            interview below is the thing you act on. */}
        {!guest && <InterviewsPanel
          labels={labels}
          project={project}
          projectsApi={projectsApi}
          pendingWorkItems={pendingWork}
        />}

        <ConnectorsPanel
          labels={labels}
          locale={locale}
          project={project}
          projectsApi={projectsApi}
          locked={connectorsLocked}
        />

        {/* The Generated / Staging / Production status boxes are gone: they
            answered questions owners never asked ("not requested", "Not ready
            for promotion"). Progress lives in "Where things stand"; the way in
            to a ready ERP lives on its primary action. */}
        {latestRun?.lastError && latestRun.status === 'failed' ? (
          <p className="text-sm text-destructive">{latestRun.lastError}</p>
        ) : null}
      </CardContent>}
    </Card>
  )
}
