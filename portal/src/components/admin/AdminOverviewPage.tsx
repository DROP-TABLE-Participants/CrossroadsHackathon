'use client'

import {
  BellRinging,
  FolderSimple,
  ShieldCheck,
  UserMinus,
  UsersThree,
  WarningCircle,
} from '@phosphor-icons/react'
import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'

import { adminApi } from '@/components/admin/api'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import { AdminStatusBadge } from '@/components/admin/AdminStatusBadge'
import type { AdminOverview } from '@/components/admin/types'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import type { Locale } from '@/i18n/config'

export function AdminOverviewPage({ locale, labels }: { locale: Locale; labels: AdminLabels }) {
  const [overview, setOverview] = useState<AdminOverview | null>(null)
  const [error, setError] = useState('')
  const load = useCallback(async () => {
    setError('')
    try {
      setOverview(await adminApi<AdminOverview>('/api/admin/overview'))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load the overview.')
    }
  }, [])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  if (error) return <AdminError message={error} labels={labels} retry={() => void load()} />
  if (!overview) return <AdminLoading labels={labels} />

  const cards = [
    { label: labels.activeCustomers, value: overview.activeCustomerCount, icon: UsersThree, href: `/${locale}/admin/customers?status=active` },
    { label: labels.inactiveCustomers, value: overview.inactiveCustomerCount, icon: UserMinus, href: `/${locale}/admin/customers?status=inactive` },
    { label: labels.totalProjects, value: overview.generationProjectCount, icon: FolderSimple, href: `/${locale}/admin/projects` },
    { label: labels.needsAttention, value: overview.needsAttentionProjectCount, icon: WarningCircle, href: `/${locale}/admin/projects` },
    { label: labels.failedProjects, value: overview.failedProjectCount, icon: WarningCircle, href: `/${locale}/admin/projects?status=failed` },
    { label: labels.pendingNotifications, value: overview.pendingNotificationCount, icon: BellRinging, href: `/${locale}/admin/demo-bookings` },
    { label: labels.failedNotifications, value: overview.failedNotificationCount, icon: BellRinging, href: `/${locale}/admin/demo-bookings` },
    { label: labels.activeAdministrators, value: overview.activeAdministratorCount, icon: ShieldCheck, href: `/${locale}/admin/administrators` },
  ]

  return (
    <>
      <AdminPageHeader title={labels.overview} description={labels.overviewDescription} />
      <section className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {cards.map((card) => (
          <Link href={card.href} key={card.label}>
            <Card className="h-full transition-colors hover:border-primary/40 hover:bg-muted/20">
              <CardContent className="flex items-center justify-between gap-4">
                <div>
                  <p className="text-sm text-muted-foreground">{card.label}</p>
                  <p className="mt-1 text-3xl font-semibold">{card.value}</p>
                </div>
                <div className="rounded-lg bg-primary/10 p-2 text-primary">
                  <card.icon className="size-5" />
                </div>
              </CardContent>
            </Card>
          </Link>
        ))}
      </section>
      <section className="grid gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader><CardTitle>{labels.recentProjects}</CardTitle></CardHeader>
          <CardContent className="grid gap-2">
            {overview.recentProjects.map((project) => (
              <Link
                href={`/${locale}/admin/projects/${project.id}`}
                key={project.id}
                className="flex items-center justify-between gap-3 rounded-lg border p-3 hover:bg-muted/40"
              >
                <div className="min-w-0">
                  <p className="truncate font-medium">{project.companyName}</p>
                  <p className="truncate text-sm text-muted-foreground">{project.customerEmail}</p>
                </div>
                <AdminStatusBadge status={project.status} />
              </Link>
            ))}
            {!overview.recentProjects.length ? (
              <p className="text-sm text-muted-foreground">{labels.noResults}</p>
            ) : null}
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle>{labels.recentActivity}</CardTitle></CardHeader>
          <CardContent className="grid gap-2">
            {overview.recentAuditRecords.map((record) => (
              <div key={record.id} className="rounded-lg border p-3">
                <div className="flex items-start justify-between gap-3">
                  <p className="font-medium">{record.action.replaceAll('_', ' ')}</p>
                  <time className="shrink-0 text-xs text-muted-foreground">
                    {new Date(record.createdAt).toLocaleString(locale)}
                  </time>
                </div>
                <p className="mt-1 text-sm text-muted-foreground">
                  {record.actorEmail} · {record.resourceType}
                </p>
              </div>
            ))}
            {!overview.recentAuditRecords.length ? (
              <p className="text-sm text-muted-foreground">{labels.noResults}</p>
            ) : null}
          </CardContent>
        </Card>
      </section>
    </>
  )
}
