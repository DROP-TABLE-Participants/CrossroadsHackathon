import { cookies } from 'next/headers'
import { notFound, redirect } from 'next/navigation'

import { locales, type Locale } from '@/i18n/config'
import { getPortalEntryPath, hasPortalSession } from '@/lib/auth/portal-entry'

export default async function PortalEntryPage({ params }: { params: Promise<{ locale: string }> }) {
  const { locale } = await params
  if (!locales.includes(locale as Locale)) notFound()

  const cookieStore = await cookies()
  redirect(getPortalEntryPath(locale as Locale, hasPortalSession(cookieStore)))
}
