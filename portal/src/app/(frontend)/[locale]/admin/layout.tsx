import { notFound } from 'next/navigation'
import type { ReactNode } from 'react'

import { AdminSessionProvider } from '@/components/admin/AdminSessionProvider'
import { locales, type Locale } from '@/i18n/config'

export default async function AdminLayout({ params, children }: { params: Promise<{ locale: string }>; children: ReactNode }) {
  const { locale } = await params
  if (!locales.includes(locale as Locale)) notFound()
  return <AdminSessionProvider>{children}</AdminSessionProvider>
}
