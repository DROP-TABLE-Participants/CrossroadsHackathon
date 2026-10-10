'use client'

import { Trash as Trash2 } from '@phosphor-icons/react'
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
import { DiscoveryJourney } from '@/components/account/DiscoveryJourney'
import { discoveryJourneyStep } from '@/components/account/helpers'
import type { AccountLabels } from '@/components/account/labels'
import type { GenerationProjectSummary } from '@/components/account/types'
import type { Locale } from '@/i18n/config'
import { ConnectorsPanel } from './ConnectorsPanel'
import { generationInFlight } from './generation-progress'
import { GenerationProgressPanel } from './GenerationProgressPanel'
import { InterviewsPanel } from './InterviewsPanel'
import { PendingWorkList } from './PendingWorkList'
import { ProjectPrimaryAction } from './ProjectPrimaryAction'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'

type ProjectCardProps = {
  labels: AccountLabels
  locale: Locale
  project: GenerationProjectSummary
  projectsApi: ProjectsApi
  highlighted?: boolean
}

export function ProjectCard({ labels, locale, project, projectsApi, highlighted }: ProjectCardProps) {
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
  const description = [project.industry, project.companyType].filter(Boolean).join(' / ')
  const pendingWork = latestRun?.pendingWorkItems || []
  const nonInterviewWork = pendingWork.filter((item) => item.kind !== 'interview_request')
  // Evidence must not shift under a run that has already collected it.
  const connectorsLocked = generationInFlight(latestRun)

  return (
    <Card
      id={`project-${project.id}`}
      className={cn('transition-shadow', highlighted && 'ring-2 ring-primary')}
    >
      <CardHeader>
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="flex min-w-0 flex-col gap-1">
            <CardTitle className="text-lg">{project.companyName}</CardTitle>
            {description ? <CardDescription>{description}</CardDescription> : null}
          </div>
          <div className="flex items-center gap-1">
            <Badge variant="secondary">{labels.projectStatus(project.status || 'draft')}</Badge>
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
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
          <span>{labels.participants}: {project.participants?.length || 0}</span>
          <span>{labels.interviews}: {project.requests?.length || 0}</span>
          <span>{labels.connectedSources}: {project.inputSourceCount || 0}</span>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-5">
        {/* "Where things stand" leads the card: the first thing an owner sees is
            what ERP Generator is doing and how far along it is. */}
        <GenerationProgressPanel labels={labels} project={project} run={latestRun} />

        <DiscoveryJourney activeStep={discoveryJourneyStep(project)} locale={locale} />

        <ProjectPrimaryAction
          labels={labels}
          locale={locale}
          project={project}
          latestRun={latestRun}
          projectsApi={projectsApi}
        />

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
      </CardContent>
    </Card>
  )
}
