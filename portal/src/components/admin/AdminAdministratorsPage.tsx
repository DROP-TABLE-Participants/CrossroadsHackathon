'use client'

import { EnvelopeSimple, MagnifyingGlass, Plus } from '@phosphor-icons/react'
import { useCallback, useEffect, useState } from 'react'
import { toast } from 'sonner'

import { adminApi } from '@/components/admin/api'
import { useAdminSession } from '@/components/admin/AdminSessionProvider'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPagination } from '@/components/admin/AdminPagination'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminEmpty, AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import { AdminStatusBadge } from '@/components/admin/AdminStatusBadge'
import type { AdminAdministrator, AdminPaged } from '@/components/admin/types'
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
import { Card, CardContent } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import type { Locale } from '@/i18n/config'

export function AdminAdministratorsPage({ locale, labels }: { locale: Locale; labels: AdminLabels }) {
  const { admin: currentAdmin } = useAdminSession()
  const [query, setQuery] = useState('')
  const [submittedQuery, setSubmittedQuery] = useState('')
  const [status, setStatus] = useState('all')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<AdminPaged<{ administrators: AdminAdministrator[] }> | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const [inviteOpen, setInviteOpen] = useState(false)
  const [inviteName, setInviteName] = useState('')
  const [inviteEmail, setInviteEmail] = useState('')

  const load = useCallback(async () => {
    setError('')
    const params = new URLSearchParams({ page: String(page), pageSize: '20' })
    if (submittedQuery) params.set('search', submittedQuery)
    if (status !== 'all') params.set('isActive', String(status === 'active'))
    try {
      setData(await adminApi(`/api/admin/administrators?${params}`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load administrators.')
    }
  }, [page, status, submittedQuery])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  const createAdministrator = async () => {
    setBusy('invite')
    try {
      await adminApi('/api/admin/administrators', {
        method: 'POST',
        body: JSON.stringify({ name: inviteName, email: inviteEmail, locale }),
      })
      setInviteName('')
      setInviteEmail('')
      setInviteOpen(false)
      toast.success(labels.inviteAccepted)
      await load()
    } catch (reason) {
      toast.error(reason instanceof Error ? reason.message : 'Unable to invite the administrator.')
    } finally {
      setBusy('')
    }
  }

  const setAdministratorStatus = async (administrator: AdminAdministrator, isActive: boolean) => {
    setBusy(`status:${administrator.id}`)
    try {
      await adminApi(`/api/admin/administrators/${administrator.id}/status`, {
        method: 'PATCH',
        body: JSON.stringify({ isActive }),
      })
      await load()
    } catch (reason) {
      toast.error(reason instanceof Error ? reason.message : 'Unable to update the administrator.')
    } finally {
      setBusy('')
    }
  }

  const resetPassword = async (administrator: AdminAdministrator) => {
    setBusy(`reset:${administrator.id}`)
    try {
      await adminApi(`/api/admin/administrators/${administrator.id}/password-reset`, {
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

  return (
    <>
      <AdminPageHeader
        title={labels.administrators}
        description={labels.administratorsDescription}
        action={(
          <Dialog open={inviteOpen} onOpenChange={setInviteOpen}>
            <DialogTrigger asChild><Button><Plus /> {labels.inviteAdministrator}</Button></DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>{labels.inviteAdministrator}</DialogTitle>
                <DialogDescription>{labels.inviteDescription}</DialogDescription>
              </DialogHeader>
              <div className="grid gap-4">
                <div className="grid gap-2">
                  <Label htmlFor="admin-name">{labels.name}</Label>
                  <Input id="admin-name" value={inviteName} onChange={(event) => setInviteName(event.target.value)} />
                </div>
                <div className="grid gap-2">
                  <Label htmlFor="admin-email">{labels.email}</Label>
                  <Input id="admin-email" type="email" value={inviteEmail} onChange={(event) => setInviteEmail(event.target.value)} />
                </div>
              </div>
              <DialogFooter>
                <Button variant="outline" onClick={() => setInviteOpen(false)}>{labels.cancel}</Button>
                <Button
                  disabled={busy === 'invite' || !inviteName.trim() || !inviteEmail.trim()}
                  onClick={() => void createAdministrator()}
                >
                  {busy === 'invite' ? labels.sending : labels.sendInvite}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        )}
      />
      <p className="rounded-lg border bg-muted/30 p-3 text-sm text-muted-foreground">{labels.adminSafety}</p>
      <form
        className="flex flex-wrap gap-2"
        onSubmit={(event) => {
          event.preventDefault()
          setPage(1)
          setSubmittedQuery(query.trim())
        }}
      >
        <div className="relative min-w-64 flex-1">
          <MagnifyingGlass className="absolute top-2.5 left-3 size-4 text-muted-foreground" />
          <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={`${labels.search}…`} className="pl-9" />
        </div>
        <Select value={status} onValueChange={(value) => { setStatus(value); setPage(1) }}>
          <SelectTrigger className="w-44"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{labels.allStatuses}</SelectItem>
            <SelectItem value="active">{labels.active}</SelectItem>
            <SelectItem value="inactive">{labels.inactive}</SelectItem>
          </SelectContent>
        </Select>
        <Button type="submit" variant="outline">{labels.search}</Button>
      </form>
      {error ? <AdminError message={error} labels={labels} retry={() => void load()} /> : null}
      {!error && !data ? <AdminLoading labels={labels} /> : null}
      {!error && data?.administrators.length === 0 ? <AdminEmpty labels={labels} /> : null}
      {data && data.administrators.length > 0 ? (
        <>
          <Card>
            <CardContent className="divide-y p-0">
              {data.administrators.map((administrator) => {
                const isCurrent = administrator.id === currentAdmin?.id
                return (
                  <div key={administrator.id} className="flex flex-wrap items-center justify-between gap-4 p-4">
                    <div>
                      <div className="flex items-center gap-2">
                        <p className="font-medium">{administrator.name || administrator.email}</p>
                        {isCurrent ? <Badge variant="outline">{labels.currentAdministrator}</Badge> : null}
                      </div>
                      <p className="text-sm text-muted-foreground">{administrator.email} · {administrator.role}</p>
                    </div>
                    <div className="flex flex-wrap items-center gap-2">
                      <AdminStatusBadge status={administrator.isActive ? labels.active : labels.inactive} />
                      <Button
                        size="sm"
                        variant="outline"
                        disabled={!administrator.isActive || busy !== ''}
                        onClick={() => void resetPassword(administrator)}
                      >
                        <EnvelopeSimple /> {labels.passwordReset}
                      </Button>
                      {administrator.isActive ? (
                        <AlertDialog>
                          <AlertDialogTrigger asChild>
                            <Button size="sm" variant="destructive" disabled={isCurrent || busy !== ''}>
                              {labels.deactivate}
                            </Button>
                          </AlertDialogTrigger>
                          <AlertDialogContent>
                            <AlertDialogHeader>
                              <AlertDialogTitle>{labels.confirmDeactivateTitle}</AlertDialogTitle>
                              <AlertDialogDescription>{labels.confirmAdministratorDeactivate}</AlertDialogDescription>
                            </AlertDialogHeader>
                            <AlertDialogFooter>
                              <AlertDialogCancel>{labels.cancel}</AlertDialogCancel>
                              <AlertDialogAction onClick={() => void setAdministratorStatus(administrator, false)}>
                                {labels.deactivate}
                              </AlertDialogAction>
                            </AlertDialogFooter>
                          </AlertDialogContent>
                        </AlertDialog>
                      ) : (
                        <Button size="sm" disabled={busy !== ''} onClick={() => void setAdministratorStatus(administrator, true)}>
                          {labels.activate}
                        </Button>
                      )}
                    </div>
                  </div>
                )
              })}
            </CardContent>
          </Card>
          <AdminPagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} labels={labels} onPage={setPage} />
        </>
      ) : null}
    </>
  )
}
