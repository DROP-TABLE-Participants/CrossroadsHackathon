import type { Metadata } from 'next'

import { AdminOverviewPage } from '@/components/admin/AdminOverviewPage'
import { getAdminLabels } from '@/components/admin/labels'
import type { Locale } from '@/i18n/config'

export const metadata: Metadata = { title: 'Administrator | ERP Generator', robots: { index: false, follow: false } }

export default async function AdminPage({ params }: { params: Promise<{ locale: Locale }> }) {
  const { locale } = await params
  return <AdminOverviewPage locale={locale} labels={getAdminLabels(locale)} />
}
