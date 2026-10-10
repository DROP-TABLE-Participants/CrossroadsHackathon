'use client'

import { ArrowLeft, EnvelopeSimple } from '@phosphor-icons/react'
import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'
import { toast } from 'sonner'

import { adminApi } from '@/components/admin/api'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import { AdminStatusBadge } from '@/components/admin/AdminStatusBadge'
import type { AdminCustomerDetails } from '@/components/admin/types'
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
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import type { Locale } from '@/i18n/config'

export function AdminCustomerDetailsPage({
  locale,
  customerId,
  labels,
}: {
  locale: Locale
  customerId: string
  labels: AdminLabels
}) {
  const [data, setData] = useState<AdminCustomerDetails | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const load = useCallback(async () => {
    setError('')
    try {
      setData(await adminApi(`/api/admin/customers/${customerId}`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load the customer.')
    }
  }, [customerId])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  const setStatus = async (isActive: boolean) => {
    setBusy('status')
    try {
      await adminApi(`/api/admin/customers/${customerId}/status`, {
        method: 'PATCH',
        body: JSON.stringify({ isActive }),
      })
      await load()
    } catch (reason) {
      toast.error(reason instanceof Error ? reason.message : 'Unable to update the customer.')
    } finally {
      setBusy('')
    }
  }
  const resetPassword = async () => {
    setBusy('reset')
    try {
      await adminApi(`/api/admin/customers/${customerId}/password-reset`, {
        method: 'POST',
        body: JSON.stringify({ locale }),
      })
      toast.success(labels.resetAccepted)
    } catch (reason) {
      toast.error(reason instanceof Error ? reason.message : 'Unable to request a password reset.')
    } finally {
      setBusy('')
    }
  }

  if (error) return <AdminError message={error} labels={labels} retry={() => void load()} />
  if (!data) return <AdminLoading labels={labels} />
  const customer = data.customer

  return (
    <>
      <Link href={`/${locale}/admin/customers`} className="flex w-fit items-center gap-2 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft /> {labels.customers}
      </Link>
      <AdminPageHeader
        title={customer.name || customer.email}
        description={labels.customerDetails}
        action={<AdminStatusBadge status={customer.isActive ? labels.active : labels.inactive} />}
      />
      <section className="grid gap-4 xl:grid-cols-[1fr_auto]">
        <Card>
          <CardHeader><CardTitle>{labels.customerDetails}</CardTitle></CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-2">
            <div><p className="text-xs text-muted-foreground">{labels.email}</p><p>{customer.email}</p></div>
            <div><p className="text-xs text-muted-foreground">{labels.company}</p><p>{customer.companyName || '—'}</p></div>
            <div><p className="text-xs text-muted-foreground">{labels.created}</p><p>{new Date(customer.createdAt).toLocaleString(locale)}</p></div>
            <div><p className="text-xs text-muted-foreground">{labels.updated}</p><p>{new Date(customer.updatedAt).toLocaleString(locale)}</p></div>
          </CardContent>
        </Card>
        <Card className="min-w-72">
          <CardHeader><CardTitle>{labels.status}</CardTitle></CardHeader>
          <CardContent className="grid gap-2">
            <Button variant="outline" disabled={!customer.isActive || busy !== ''} onClick={() => void resetPassword()}>
              <EnvelopeSimple /> {labels.passwordReset}
            </Button>
            {customer.isActive ? (
              <AlertDialog>
                <AlertDialogTrigger asChild>
                  <Button variant="destructive" disabled={busy !== ''}>{labels.deactivate}</Button>
                </AlertDialogTrigger>
                <AlertDialogContent>
                  <AlertDialogHeader>
                    <AlertDialogTitle>{labels.confirmDeactivateTitle}</AlertDialogTitle>
                    <AlertDialogDescription>{labels.confirmCustomerDeactivate}</AlertDialogDescription>
                  </AlertDialogHeader>
                  <AlertDialogFooter>
                    <AlertDialogCancel>{labels.cancel}</AlertDialogCancel>
                    <AlertDialogAction onClick={() => void setStatus(false)}>{labels.deactivate}</AlertDialogAction>
                  </AlertDialogFooter>
                </AlertDialogContent>
              </AlertDialog>
            ) : (
              <Button disabled={busy !== ''} onClick={() => void setStatus(true)}>{labels.activate}</Button>
            )}
          </CardContent>
        </Card>
      </section>
      <Card>
        <CardHeader><CardTitle>{labels.projectsOwned}</CardTitle></CardHeader>
        <CardContent className="grid gap-2">
          {data.projects.length ? data.projects.map((project) => (
            <Link
              href={`/${locale}/admin/projects/${project.id}`}
              key={project.id}
              className="flex items-center justify-between gap-3 rounded-lg border p-3 hover:bg-muted/40"
            >
              <div>
                <p className="font-medium">{project.companyName}</p>
                <p className="text-sm text-muted-foreground">{project.discoveryMode.replaceAll('_', ' ')}</p>
              </div>
              <AdminStatusBadge status={project.status} />
            </Link>
          )) : <p className="text-sm text-muted-foreground">{labels.noResults}</p>}
        </CardContent>
      </Card>
      <Card>
        <CardHeader><CardTitle>{labels.recentActivity}</CardTitle></CardHeader>
        <CardContent className="grid gap-2">
          {data.recentAuditRecords.length ? data.recentAuditRecords.map((record) => (
            <div key={record.id} className="rounded-lg border p-3">
              <p className="font-medium">{record.action.replaceAll('_', ' ')}</p>
              <p className="text-sm text-muted-foreground">
                {record.actorEmail} · {new Date(record.createdAt).toLocaleString(locale)}
              </p>
            </div>
          )) : <p className="text-sm text-muted-foreground">{labels.noResults}</p>}
        </CardContent>
      </Card>
    </>
  )
}
