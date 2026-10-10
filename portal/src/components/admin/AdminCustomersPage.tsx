'use client'

import { MagnifyingGlass } from '@phosphor-icons/react'
import Link from 'next/link'
import { useSearchParams } from 'next/navigation'
import { useCallback, useEffect, useState } from 'react'

import { adminApi } from '@/components/admin/api'
import type { AdminLabels } from '@/components/admin/labels'
import { AdminPagination } from '@/components/admin/AdminPagination'
import { AdminPageHeader } from '@/components/admin/AdminPageHeader'
import { AdminEmpty, AdminError, AdminLoading } from '@/components/admin/AdminPageState'
import { AdminStatusBadge } from '@/components/admin/AdminStatusBadge'
import type { AdminCustomer, AdminPaged } from '@/components/admin/types'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import type { Locale } from '@/i18n/config'

export function AdminCustomersPage({ locale, labels }: { locale: Locale; labels: AdminLabels }) {
  const searchParams = useSearchParams()
  const initialStatus = searchParams.get('status') ?? 'all'
  const [query, setQuery] = useState('')
  const [submittedQuery, setSubmittedQuery] = useState('')
  const [status, setStatus] = useState(initialStatus)
  const [page, setPage] = useState(1)
  const [data, setData] = useState<AdminPaged<{ customers: AdminCustomer[] }> | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    setError('')
    const params = new URLSearchParams({ page: String(page), pageSize: '20' })
    if (submittedQuery) params.set('search', submittedQuery)
    if (status !== 'all') params.set('isActive', String(status === 'active'))
    try {
      setData(await adminApi(`/api/admin/customers?${params}`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load customers.')
    }
  }, [page, status, submittedQuery])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  return (
    <>
      <AdminPageHeader title={labels.customers} description={labels.customersDescription} />
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
          <Input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder={`${labels.search}…`}
            className="pl-9"
          />
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
      {!error && data?.customers.length === 0 ? <AdminEmpty labels={labels} /> : null}
      {data && data.customers.length > 0 ? (
        <>
          <Card>
            <CardContent className="divide-y p-0">
              {data.customers.map((customer) => (
                <Link
                  href={`/${locale}/admin/customers/${customer.id}`}
                  key={customer.id}
                  className="flex flex-wrap items-center justify-between gap-4 p-4 hover:bg-muted/40"
                >
                  <div className="min-w-0">
                    <p className="font-medium">{customer.name || customer.email}</p>
                    <p className="truncate text-sm text-muted-foreground">
                      {customer.email}{customer.companyName ? ` · ${customer.companyName}` : ''}
                    </p>
                  </div>
                  <div className="flex items-center gap-4">
                    <span className="text-sm text-muted-foreground">
                      {customer.generationProjectCount} {labels.projects.toLowerCase()}
                    </span>
                    <AdminStatusBadge status={customer.isActive ? labels.active : labels.inactive} />
                  </div>
                </Link>
              ))}
            </CardContent>
          </Card>
          <AdminPagination
            page={data.page}
            pageSize={data.pageSize}
            totalCount={data.totalCount}
            labels={labels}
            onPage={setPage}
          />
        </>
      ) : null}
    </>
  )
}
