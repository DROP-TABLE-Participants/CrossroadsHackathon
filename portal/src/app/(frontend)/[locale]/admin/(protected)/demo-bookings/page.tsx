import type { Metadata } from 'next'

import { AdminDemoBookingsPage } from '@/components/admin/AdminDemoBookingsPage'
import { getAdminLabels } from '@/components/admin/labels'
import type { Locale } from '@/i18n/config'

export const metadata: Metadata = { title: 'Demo Bookings | ERP Generator Administration', robots: { index: false, follow: false } }

export default async function DemoBookingsPage({ params }: { params: Promise<{ locale: Locale }> }) {
  const { locale } = await params
  return <AdminDemoBookingsPage locale={locale} labels={getAdminLabels(locale)} />
}
