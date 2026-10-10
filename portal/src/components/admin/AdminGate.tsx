'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'
import type { ReactNode } from 'react'

import { useAdminSession } from '@/components/admin/AdminSessionProvider'
import { Spinner } from '@/components/ui/spinner'
import type { Locale } from '@/i18n/config'

export function AdminGate({ locale, children }: { locale: Locale; children: ReactNode }) {
  const { admin, sessionResolved } = useAdminSession()
  const router = useRouter()

  useEffect(() => {
    if (!sessionResolved) return
    if (!admin) {
      router.replace(`/${locale}/admin/login?next=${encodeURIComponent(`/${locale}/admin`)}`)
    }
  }, [admin, locale, router, sessionResolved])

  if (!sessionResolved || !admin) {
    return (
      <div className="flex min-h-svh items-center justify-center gap-2 bg-background text-muted-foreground">
        <Spinner className="size-4" />
        <span className="text-sm">Checking administrator access…</span>
      </div>
    )
  }
  return children
}
