'use client'

import Link from 'next/link'
import {
  ArrowRight,
  ChatsCircle,
  Cpu,
  PlugsConnected,
  Plus,
  ArrowsClockwise as RefreshCw,
  RocketLaunch,
  Sparkle,
} from '@phosphor-icons/react'
import { useEffect, useMemo, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { useGenerationProjects } from '@/components/account/hooks/useGenerationProjects'
import { getLabels } from '@/components/account/labels'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { cn } from '@/lib/utils'
import { DandelionDemoCard } from '@/components/account/demo/DandelionDemoCard'
import { ProjectCard } from '@/components/account/projects/ProjectCard'
import type { Locale } from '@/i18n/config'

/**
 * Dashboard (the /account landing page): the customer's ERP projects with one
 * primary action each. With no projects yet, a centered "Create your ERP"
 * hero is THE key action.
 */
export function DashboardContent({ locale }: { locale: Locale }) {
  return <><DandelionDemoCard locale={locale} /><GeneratedProjectsContent locale={locale} /></>
}

function GeneratedProjectsContent({ locale }: { locale: Locale }) {
  const { customer, openLogin } = useDemoBooking()
  const labels = useMemo(() => getLabels(locale), [locale])
  const [createdProjectId, setCreatedProjectId] = useState<string | null>(null)
  // Which ERP's tab is open. Null means "follow the list" — a freshly created
  // project should surface itself rather than leave the old tab selected.
  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null)
  const [initialized, setInitialized] = useState(false)

  const projectsApi = useGenerationProjects({ labels, openLogin, customer })
  const { projects, setProjects, projectsLoading, refreshProjects } = projectsApi

  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    const created = params.get('created')
    setCreatedProjectId(created)
    // Land on the ERP you just created, not whichever tab happens to be first.
    if (created) setSelectedProjectId(created)
  }, [])

  useEffect(() => {
    if (!customer) {
      setProjects([])
      return
    }
    Promise.resolve(refreshProjects()).finally(() => setInitialized(true))
  }, [customer, refreshProjects, setProjects])

  if (!initialized) {
    return (
      <section aria-label={labels.discoveryTitle} className="flex flex-col gap-4" aria-busy="true">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40 w-full rounded-xl" />
        <Skeleton className="h-40 w-full rounded-xl" />
      </section>
    )
  }

  if (projects.length === 0) return <Card className="p-6"><h2 className="font-semibold">{locale === 'bg' ? 'Генерирани ERP системи' : 'Generated ERPs'}</h2><p className="my-3 text-sm text-muted-foreground">{locale === 'bg' ? 'Опишете бизнеса си или персонализирайте Dandelion. Подготвеното демо остава достъпно.' : 'Describe your business or customize Dandelion. Your prepared demo stays available.'}</p><Button asChild variant="outline"><Link href={`/${locale}/account/create`}>{locale === 'bg' ? 'Създай ERP' : 'Create ERP'}</Link></Button></Card>

  // Tabs only earn their keep with more than one ERP; a single project should
  // not make someone click a tab to see the only thing they have.
  const activeProjectId = selectedProjectId && projects.some((project) => project.id === selectedProjectId)
    ? selectedProjectId
    : projects[0]?.id

  return (
    <section aria-label={labels.discoveryTitle} className="flex flex-col gap-4">
      <div className="flex items-center justify-between gap-2">
        <h1 className="text-2xl font-semibold">{labels.discoveryTitle}</h1>
        <Button
          type="button"
          size="icon"
          variant="ghost"
          onClick={refreshProjects}
          disabled={projectsLoading}
          aria-label={labels.refreshProjects}
          title={labels.refreshProjects}
        >
          <RefreshCw className={projectsLoading ? 'animate-spin' : undefined} />
        </Button>
      </div>

      {/* The ERPs themselves replace the old explainer paragraph: by the time you
          have one, you don't need to be told what ERP Generator does. */}
      <Tabs
        value={activeProjectId}
        onValueChange={setSelectedProjectId}
        className="flex flex-col gap-4"
      >
        <div className="flex flex-wrap items-center justify-between gap-3">
          {/* Pills in a muted trough: the active ERP lifts out in brand teal,
              the rest recede. The stock bordered look read as a 90s tab strip. */}
          <TabsList className="h-auto flex-wrap gap-1 rounded-lg bg-muted p-1">
            {projects.map((project) => (
              <TabsTrigger
                key={project.id}
                value={project.id}
                className={cn(
                  'rounded-md border-0 px-3.5 py-1.5 text-sm font-medium text-muted-foreground shadow-none transition-colors',
                  'hover:text-foreground',
                  // The dark: twin is required, not belt-and-braces: the vendored
                  // trigger ships `dark:data-[state=active]:bg-input/30`, which
                  // tailwind-merge can't reconcile with an un-prefixed class, so
                  // it would win wherever the OS prefers dark.
                  'data-[state=active]:bg-primary data-[state=active]:text-primary-foreground data-[state=active]:shadow-sm',
                  'dark:data-[state=active]:bg-primary dark:data-[state=active]:text-primary-foreground dark:data-[state=active]:border-transparent',
                )}
              >
                <span className="max-w-44 truncate">{project.companyName}</span>
              </TabsTrigger>
            ))}
          </TabsList>

          <Button size="sm" asChild>
            <Link href={`/${locale}/account/create`}>
              <Plus data-icon="inline-start" />
              {labels.createNewErp}
            </Link>
          </Button>
        </div>

        {projects.map((project) => (
          <TabsContent key={project.id} value={project.id} className="m-0">
            <ProjectCard
              labels={labels}
              locale={locale}
              project={project}
              projectsApi={projectsApi}
              highlighted={createdProjectId === project.id}
            />
          </TabsContent>
        ))}
      </Tabs>
    </section>
  )
}
