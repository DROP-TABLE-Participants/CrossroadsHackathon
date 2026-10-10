'use client'

import Link from 'next/link'
import { Plus, ArrowsClockwise } from '@phosphor-icons/react'
import { useEffect, useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { useGenerationProjects } from '@/components/account/hooks/useGenerationProjects'
import { getLabels } from '@/components/account/labels'
import { DandelionDemoCard } from '@/components/account/demo/DandelionDemoCard'
import { ProjectCard } from '@/components/account/projects/ProjectCard'
import type { Locale } from '@/i18n/config'

export function DashboardContent({ locale }: { locale: Locale }) {
  const { customer, openLogin } = useDemoBooking()
  const labels = useMemo(() => getLabels(locale), [locale])
  const [createdProjectId, setCreatedProjectId] = useState<string | null>(null)
  const [expandedProjectId, setExpandedProjectId] = useState<string | null>(null)
  const [initialized, setInitialized] = useState(false)
  const projectsApi = useGenerationProjects({ labels, openLogin, customer })
  const { projects, setProjects, projectsLoading, refreshProjects } = projectsApi

  useEffect(() => {
    const created = new URLSearchParams(window.location.search).get('created')
    setCreatedProjectId(created)
    setExpandedProjectId(created)
  }, [])

  useEffect(() => {
    if (!customer) { setProjects([]); return }
    Promise.resolve(refreshProjects()).finally(() => setInitialized(true))
  }, [customer, refreshProjects, setProjects])

  useEffect(() => {
    if (initialized && createdProjectId) {
      document.getElementById(`project-${createdProjectId}`)?.scrollIntoView({ block: 'nearest' })
    }
  }, [initialized, createdProjectId])

  return <>
    <div className="flex flex-wrap items-center justify-between gap-3">
      <h1 className="text-2xl font-semibold">{locale === 'bg' ? 'Моите ERP системи' : 'My ERPs'}</h1>
      <Button asChild><Link href={`/${locale}/account/create`}><Plus />{locale === 'bg' ? 'Създай ERP' : 'Create ERP'}</Link></Button>
    </div>
    <DandelionDemoCard locale={locale} />
    <section aria-label={labels.discoveryTitle} className="min-w-0" aria-busy={!initialized || projectsLoading}>
      <div className="mb-2 flex items-center justify-between gap-2">
        <h2 className="text-base font-semibold">{locale === 'bg' ? 'Генерирани ERP системи' : 'Generated ERPs'}</h2>
        <Button type="button" size="icon-sm" variant="ghost" onClick={refreshProjects} disabled={projectsLoading} aria-label={labels.refreshProjects} title={labels.refreshProjects}>
          <ArrowsClockwise className={projectsLoading ? 'animate-spin' : undefined} />
        </Button>
      </div>
      {!initialized ? <div className="space-y-2"><Skeleton className="h-20 w-full rounded-lg" /><Skeleton className="h-20 w-full rounded-lg" /></div>
        : projects.length === 0 ? <p className="rounded-lg border p-4 text-sm text-muted-foreground">{locale === 'bg' ? 'Все още няма генерирани ERP системи.' : 'No generated ERPs yet.'}</p>
        : <div className="overflow-hidden rounded-lg border">{projects.map(project => <ProjectCard
          key={project.id} labels={labels} locale={locale} project={project} projectsApi={projectsApi}
          highlighted={project.id === createdProjectId} expanded={project.id === expandedProjectId}
          onExpandedChange={() => setExpandedProjectId(current => current === project.id ? null : project.id)}
        />)}</div>}
    </section>
  </>
}
