import type { ReactNode } from 'react'
import { cookies } from 'next/headers'
import { redirect } from 'next/navigation'

import { AdminGate } from '@/components/admin/AdminGate'
import { AdminSidebarShell } from '@/components/admin/AdminSidebarShell'
import { getAdminLabels } from '@/components/admin/labels'
import type { Locale } from '@/i18n/config'
import { hasAdminSession } from '@/lib/auth/portal-entry'

export default async function ProtectedAdminLayout({
  params,
  children,
}: {
  params: Promise<{ locale: string }>
  children: ReactNode
}) {
  const { locale } = await params
  if (!hasAdminSession(await cookies())) {
    redirect(`/${locale}/admin/login?next=${encodeURIComponent(`/${locale}/admin`)}`)
  }
  const typedLocale = locale as Locale
  return (
    <AdminGate locale={typedLocale}>
      <AdminSidebarShell locale={typedLocale} labels={getAdminLabels(typedLocale)}>
        {children}
      </AdminSidebarShell>
    </AdminGate>
  )
}
