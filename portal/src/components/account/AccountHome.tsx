'use client'

import Link from 'next/link'
import { CalendarDots as CalendarDays, ClipboardText as ClipboardList, PlusCircle, ArrowsClockwise as RefreshCw, User as UserRound } from '@phosphor-icons/react'
import { useEffect, useMemo, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { AccountShell } from '@/components/account/AccountShell'
import {
  formatDateRange,
  humanRoleLabel,
  resolveGeneratedBundleSelection,
  slugifyTenant,
} from '@/components/account/helpers'
import { useErpFeedback } from '@/components/account/hooks/useErpFeedback'
import { useGenerationProjects } from '@/components/account/hooks/useGenerationProjects'
import { useProfile } from '@/components/account/hooks/useProfile'
import { useTenants } from '@/components/account/hooks/useTenants'
import { getLabels } from '@/components/account/labels'
import { FeedbackPanel } from '@/components/account/feedback/FeedbackPanel'
import { ProfileCard } from '@/components/account/profile/ProfileCard'
import { ProjectCard } from '@/components/account/projects/ProjectCard'
import type { GeneratedBundleOption } from '@/components/account/types'
import type { Locale } from '@/i18n/config'

export function AccountHome({ locale }: { locale: Locale }) {
  const { activeBooking, customer, logout, openBooking, openLogin, refreshCustomer } = useDemoBooking()
  const labels = useMemo(() => getLabels(locale), [locale])
  const [profileOpen, setProfileOpen] = useState(false)
  const [createdProjectId, setCreatedProjectId] = useState<string | null>(null)

  const profile = useProfile({ labels, openLogin, refreshCustomer })
  const {
    name,
    email,
    position,
    setName,
    setEmail,
    setPhoneNumber,
    setCompanyName,
    setPosition,
  } = profile

  const projectsApi = useGenerationProjects({ labels, openLogin, customer })
  const {
    projects,
    setProjects,
    projectsLoading,
    refreshProjects,
    setActivationLinks,
  } = projectsApi

  const generatedBundleOptions = useMemo<GeneratedBundleOption[]>(() => {
    return projects.flatMap((project) =>
      (project.generationRuns || [])
        .filter((run) =>
          Boolean(run.generatedErp) &&
          ['completed', 'staging_ready', 'production_ready'].includes(run.status))
        .map((run) => ({
          id: `generated:${run.id}`,
          runId: run.id,
          projectId: project.id,
          label: `${project.companyName} ${project.industry ? humanRoleLabel(project.industry) : ''} ERP`.replace(/\s+/g, ' ').trim(),
          entityCount: run.generatedErp?.entities.length || 0,
          workflowCount: run.generatedErp?.workflows.length || 0,
          reportCount: run.generatedErp?.reports.length || 0,
          status: run.production?.status === 'ready'
            ? 'deployed_production'
            : run.staging?.status === 'ready'
              ? 'deployed_staging'
              : 'compiled',
        })),
    )
  }, [projects])

  const tenantsApi = useTenants({
    labels,
    openLogin,
    companyName: profile.companyName,
    generatedBundleOptions,
    setActivationLinks,
    refreshProjects,
  })
  const { setTenants, setTenantSlug, tenantBundleSelection, setTenantBundleSelection, refreshTenants } = tenantsApi

  const feedbackApi = useErpFeedback({
    labels,
    openLogin,
    setTenantError: tenantsApi.setTenantError,
    setTenantFeedback: tenantsApi.setTenantFeedback,
    refreshTenants,
    refreshProjects,
  })
  const {
    setErpFeedbackItems,
    setErpUsageObservations,
    setErpChangeProposals,
    setErpChangeJobs,
    refreshErpFeedback,
  } = feedbackApi

  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    setCreatedProjectId(params.get('created'))
  }, [])

  useEffect(() => {
    if (!customer) return

    setName(customer.name || '')
    setEmail(customer.email || '')
    setPhoneNumber(customer.phoneNumber || '')
    setCompanyName(customer.companyName || '')
    setPosition(customer.position || '')
    setTenantSlug((current) => current || slugifyTenant(customer.companyName || ''))
  }, [customer, setCompanyName, setEmail, setName, setPhoneNumber, setPosition, setTenantSlug])

  useEffect(() => {
    if (!customer) {
      setTenants([])
      setProjects([])
      setErpFeedbackItems([])
      setErpUsageObservations([])
      setErpChangeProposals([])
      setErpChangeJobs([])
      return
    }

    refreshTenants()
    refreshErpFeedback()
    refreshProjects()
  }, [customer, refreshErpFeedback, refreshProjects, refreshTenants, setErpChangeJobs, setErpChangeProposals, setErpFeedbackItems, setErpUsageObservations, setProjects, setTenants])

  useEffect(() => {
    const nextSelection = resolveGeneratedBundleSelection(tenantBundleSelection, generatedBundleOptions)
    if (tenantBundleSelection !== nextSelection) {
      setTenantBundleSelection(nextSelection)
    }
  }, [generatedBundleOptions, setTenantBundleSelection, tenantBundleSelection])

  const dateRange = formatDateRange(locale, activeBooking?.startDate, activeBooking?.endDate)

  return (
    <AccountShell
      locale={locale}
      labels={labels}
      customerName={name || customer?.name || ''}
      customerEmail={email || customer?.email}
      onLogout={logout}
      onOpenProfile={() => {
        setProfileOpen(true)
        requestAnimationFrame(() => {
          document.getElementById('account-profile')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
        })
      }}
    >
      <header className="flex flex-col gap-1">
        <span className="text-xs font-semibold uppercase tracking-wide text-primary">{labels.eyebrow}</span>
        <h1 className="text-2xl font-semibold">{labels.title}</h1>
        <p className="text-sm text-muted-foreground">{labels.description}</p>
      </header>

      <section aria-label={labels.discoveryTitle} className="flex flex-col gap-4">
        <div className="flex items-center justify-between gap-2">
          <h2 className="text-lg font-semibold">{labels.discoveryTitle}</h2>
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

        {projects.length > 0 ? (
          <>
            <div className="flex flex-col gap-4">
              {projects.map((project) => (
                <ProjectCard
                  key={project.id}
                  labels={labels}
                  locale={locale}
                  project={project}
                  projectsApi={projectsApi}
                  highlighted={createdProjectId === project.id}
                />
              ))}
            </div>
            <div>
              <Button variant="outline" asChild>
                <Link href={`/${locale}/account/create`}>
                  <PlusCircle data-icon="inline-start" />
                  {labels.createAnotherErp}
                </Link>
              </Button>
            </div>
          </>
        ) : (
          <Card>
            <CardHeader className="items-start">
              <div className="mb-1 flex size-10 items-center justify-center rounded-full bg-primary/10 text-primary">
                <PlusCircle className="size-5" aria-hidden="true" />
              </div>
              <CardTitle>{labels.createYourErp}</CardTitle>
              <CardDescription>{labels.emptyStateExplainer}</CardDescription>
            </CardHeader>
            <CardContent>
              <Button size="lg" asChild>
                <Link href={`/${locale}/account/create`}>
                  <PlusCircle data-icon="inline-start" />
                  {labels.createYourErp}
                </Link>
              </Button>
            </CardContent>
          </Card>
        )}
      </section>

      <FeedbackPanel labels={labels} feedbackApi={feedbackApi} tenantsApi={tenantsApi} />

      <section id="account-profile" aria-label={labels.profileTitle}>
        <details
          className="rounded-xl border bg-card"
          open={profileOpen}
          onToggle={(event) => setProfileOpen((event.currentTarget as HTMLDetailsElement).open)}
        >
          <summary className="flex cursor-pointer list-none items-center gap-2 p-4 [&::-webkit-details-marker]:hidden">
            <UserRound className="size-5 text-muted-foreground" aria-hidden="true" />
            <h2 className="text-base font-semibold">{labels.profileTitle}</h2>
            <span className="ml-auto text-xs text-muted-foreground">{labels.accountSettings}</span>
          </summary>
          <div className="border-t p-4">
            <ProfileCard labels={labels} profile={profile} />
          </div>
        </details>
      </section>

      <section aria-label={labels.demoTitle}>
        <details className="rounded-xl border bg-card">
          <summary className="flex cursor-pointer list-none items-center gap-2 p-4 [&::-webkit-details-marker]:hidden">
            <CalendarDays className="size-5 text-muted-foreground" aria-hidden="true" />
            <h2 className="text-base font-semibold">{labels.demoTitle}</h2>
            <span className="ml-auto text-xs text-muted-foreground">
              {activeBooking ? labels.currentDemo : labels.demoAndSupport}
            </span>
          </summary>
          <div className="border-t p-4">
            {activeBooking ? (
              <div className="flex flex-col gap-1">
                <span className="text-xs text-muted-foreground">{labels.currentDemo}</span>
                {dateRange ? <strong className="text-sm font-semibold">{dateRange}</strong> : null}
                {activeBooking.status ? (
                  <p className="text-sm text-muted-foreground">{labels.status}: {activeBooking.status}</p>
                ) : null}
              </div>
            ) : (
              <div className="flex flex-col items-start gap-3">
                <p className="text-sm text-muted-foreground">{labels.noDemo}</p>
                <Button type="button" onClick={() => openBooking()}>
                  <ClipboardList data-icon="inline-start" />
                  {labels.bookDemo}
                </Button>
              </div>
            )}
          </div>
        </details>
      </section>
    </AccountShell>
  )
}
