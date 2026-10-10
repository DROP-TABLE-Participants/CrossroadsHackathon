'use client'

import Link from 'next/link'
import { ArrowRight, ChatsCircle } from '@phosphor-icons/react'
import { useEffect, useMemo, useState } from 'react'

import { Button } from '@/components/ui/button'
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Skeleton } from '@/components/ui/skeleton'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { humanRoleLabel, resolveGeneratedBundleSelection } from '@/components/account/helpers'
import { useErpFeedback } from '@/components/account/hooks/useErpFeedback'
import { useGenerationProjects } from '@/components/account/hooks/useGenerationProjects'
import { useTenants } from '@/components/account/hooks/useTenants'
import { getLabels } from '@/components/account/labels'
import { FeedbackPanel } from '@/components/account/feedback/FeedbackPanel'
import type { GeneratedBundleOption } from '@/components/account/types'
import type { Locale } from '@/i18n/config'

/**
 * /account/feedback — ERP feedback, usage observations, and change
 * proposals/jobs as a dedicated page. Wires the same hook graph the stacked
 * account page uses (projects → tenants → feedback).
 */
export function FeedbackContent({ locale }: { locale: Locale }) {
  const { customer, openLogin } = useDemoBooking()
  const labels = useMemo(() => getLabels(locale), [locale])
  // Gates the "no ERP yet" empty state — held back until the first projects
  // fetch resolves so customers who DO have an ERP never see it flash.
  const [initialized, setInitialized] = useState(false)

  const projectsApi = useGenerationProjects({ labels, openLogin, customer })
  const { projects, setProjects, refreshProjects, setActivationLinks } = projectsApi

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
    companyName: customer?.companyName || '',
    generatedBundleOptions,
    setActivationLinks,
    refreshProjects,
  })
  const { setTenants, tenantBundleSelection, setTenantBundleSelection, refreshTenants } = tenantsApi

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
    Promise.resolve(refreshProjects()).finally(() => setInitialized(true))
  }, [customer, refreshErpFeedback, refreshProjects, refreshTenants, setErpChangeJobs, setErpChangeProposals, setErpFeedbackItems, setErpUsageObservations, setProjects, setTenants])

  useEffect(() => {
    const nextSelection = resolveGeneratedBundleSelection(tenantBundleSelection, generatedBundleOptions)
    if (tenantBundleSelection !== nextSelection) {
      setTenantBundleSelection(nextSelection)
    }
  }, [generatedBundleOptions, setTenantBundleSelection, tenantBundleSelection])

  if (!initialized) {
    return (
      <section aria-label={labels.feedbackImprovements} className="flex flex-col gap-4" aria-busy="true">
        <Skeleton className="h-8 w-72" />
        <Skeleton className="h-56 w-full rounded-xl" />
      </section>
    )
  }

  // Nothing to analyse until there's an ERP: show the real panel blurred behind
  // an overlay so the feature is visible-but-unavailable, with the way forward.
  if (projects.length === 0) {
    return (
      <div className="relative">
        <div
          aria-hidden="true"
          inert
          className="pointer-events-none select-none blur-[5px] saturate-50"
        >
          <FeedbackPanel
            labels={labels}
            feedbackApi={feedbackApi}
            tenantsApi={tenantsApi}
            layout="page"
          />
        </div>

        <div className="absolute inset-0 flex items-center justify-center p-4">
          <Empty className="w-full max-w-md rounded-xl border bg-card/95 py-10 shadow-lg backdrop-blur-sm">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <ChatsCircle aria-hidden="true" />
              </EmptyMedia>
              <EmptyTitle>{labels.feedbackLockedHint}</EmptyTitle>
              <EmptyDescription>{labels.feedbackEmptyDescription}</EmptyDescription>
            </EmptyHeader>
            <EmptyContent>
              <Button asChild>
                <Link href={`/${locale}/account/create`}>
                  {labels.createYourErp}
                  <ArrowRight data-icon="inline-end" />
                </Link>
              </Button>
            </EmptyContent>
          </Empty>
        </div>
      </div>
    )
  }

  return <FeedbackPanel labels={labels} feedbackApi={feedbackApi} tenantsApi={tenantsApi} layout="page" />
}
