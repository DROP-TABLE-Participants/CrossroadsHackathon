import type { Metadata } from 'next'

import { AdminCustomersPage } from '@/components/admin/AdminCustomersPage'
import { getAdminLabels } from '@/components/admin/labels'
import type { Locale } from '@/i18n/config'

export const metadata: Metadata = { title: 'Customers | ERP Generator Administration', robots: { index: false, follow: false } }

export default async function CustomersPage({ params }: { params: Promise<{ locale: Locale }> }) {
  const { locale } = await params
  return <AdminCustomersPage locale={locale} labels={getAdminLabels(locale)} />
}
