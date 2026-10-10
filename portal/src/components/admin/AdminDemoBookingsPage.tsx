'use client'

import { MagnifyingGlass } from '@phosphor-icons/react'
import { useCallback, useEffect, useState } from 'react'
import { toast } from 'sonner'

import { adminApi } from '@/components/admin/api'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPagination } from '@/components/admin/AdminPagination'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminEmpty, AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import { AdminStatusBadge } from '@/components/admin/AdminStatusBadge'
import type { AdminDemoBooking, AdminPaged } from '@/components/admin/types'
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
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import type { Locale } from '@/i18n/config'

export function AdminDemoBookingsPage({ locale, labels }: { locale: Locale; labels: AdminLabels }) {
  const [query, setQuery] = useState('')
  const [submittedQuery, setSubmittedQuery] = useState('')
  const [bookingStatus, setBookingStatus] = useState('all')
  const [notificationStatus, setNotificationStatus] = useState('all')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<AdminPaged<{ bookings: AdminDemoBooking[] }> | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const load = useCallback(async () => {
    setError('')
    const params = new URLSearchParams({ page: String(page), pageSize: '20' })
    if (submittedQuery) params.set('search', submittedQuery)
    if (bookingStatus !== 'all') params.set('status', bookingStatus)
    if (notificationStatus !== 'all') params.set('notificationStatus', notificationStatus)
    try {
      setData(await adminApi(`/api/admin/demo-bookings?${params}`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load demo bookings.')
    }
  }, [bookingStatus, notificationStatus, page, submittedQuery])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  const resend = async (notificationId: string) => {
    setBusy(notificationId)
    try {
      await adminApi(`/api/admin/notification-deliveries/${notificationId}/resend`, { method: 'POST' })
      await load()
    } catch (reason) {
      toast.error(reason instanceof Error ? reason.message : 'Unable to resend the notification.')
    } finally {
      setBusy('')
    }
  }

  return (
    <>
      <AdminPageHeader title={labels.demoBookings} description={labels.demoDescription} />
      <form
        className="flex flex-wrap gap-2"
        onSubmit={(event) => {
          event.preventDefault()
          setPage(1)
          setSubmittedQuery(query.trim())
        }}
      >
        <div className="relative flex-1">
          <MagnifyingGlass className="absolute top-2.5 left-3 size-4 text-muted-foreground" />
          <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={`${labels.search}…`} className="pl-9" />
        </div>
        <Select value={bookingStatus} onValueChange={(value) => { setBookingStatus(value); setPage(1) }}>
          <SelectTrigger className="w-44"><SelectValue placeholder={labels.bookingStatus} /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{labels.bookingStatus}: {labels.allStatuses}</SelectItem>
            {['reserved', 'confirmed', 'cancelled', 'completed'].map((value) => (
              <SelectItem key={value} value={value}>{value}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={notificationStatus} onValueChange={(value) => { setNotificationStatus(value); setPage(1) }}>
          <SelectTrigger className="w-48"><SelectValue placeholder={labels.notificationStatus} /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{labels.notificationStatus}: {labels.allStatuses}</SelectItem>
            {['pending', 'processing', 'sent', 'failed'].map((value) => (
              <SelectItem key={value} value={value}>{value}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button type="submit" variant="outline">{labels.search}</Button>
      </form>
      {error ? <AdminError message={error} labels={labels} retry={() => void load()} /> : null}
      {!error && !data ? <AdminLoading labels={labels} /> : null}
      {!error && data?.bookings.length === 0 ? <AdminEmpty labels={labels} /> : null}
      {data && data.bookings.length > 0 ? (
        <>
          <div className="grid gap-4">
            {data.bookings.map((booking) => (
              <Card key={booking.id}>
                <CardHeader>
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                      <CardTitle>{booking.companyName} · {booking.representativeName}</CardTitle>
                      <p className="mt-1 text-sm text-muted-foreground">
                        {booking.email} · {new Date(booking.startDate).toLocaleString(locale)}
                      </p>
                    </div>
                    <AdminStatusBadge status={booking.status} />
                  </div>
                </CardHeader>
                <CardContent className="grid gap-2">
                  <p className="text-sm font-medium">{labels.notifications}</p>
                  {booking.notifications.map((notification) => (
                    <div key={notification.id} className="rounded-lg border p-3">
                      <div className="flex flex-wrap items-center justify-between gap-3">
                        <div>
                          <p className="font-medium">{notification.kind}</p>
                          <p className="text-sm text-muted-foreground">
                            {labels.recipient}: {notification.recipient} · {labels.attempts}: {notification.attemptCount}
                          </p>
                        </div>
                        <div className="flex items-center gap-2">
                          <AdminStatusBadge status={notification.status} />
                          <AlertDialog>
                            <AlertDialogTrigger asChild>
                              <Button size="sm" variant="outline" disabled={notification.status === 'processing' || busy !== ''}>
                                {labels.resend}
                              </Button>
                            </AlertDialogTrigger>
                            <AlertDialogContent>
                              <AlertDialogHeader>
                                <AlertDialogTitle>{labels.confirmResendTitle}</AlertDialogTitle>
                                <AlertDialogDescription>{labels.confirmResendDescription}</AlertDialogDescription>
                              </AlertDialogHeader>
                              <AlertDialogFooter>
                                <AlertDialogCancel>{labels.cancel}</AlertDialogCancel>
                                <AlertDialogAction onClick={() => void resend(notification.id)}>{labels.resend}</AlertDialogAction>
                              </AlertDialogFooter>
                            </AlertDialogContent>
                          </AlertDialog>
                        </div>
                      </div>
                      {notification.lastError ? <p className="mt-2 text-sm text-destructive">{notification.lastError}</p> : null}
                    </div>
                  ))}
                </CardContent>
              </Card>
            ))}
          </div>
          <AdminPagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} labels={labels} onPage={setPage} />
        </>
      ) : null}
    </>
  )
}
