import type { Metadata } from 'next'

import { AdminCustomerDetailsPage } from '@/components/admin/AdminCustomerDetailsPage'
import { getAdminLabels } from '@/components/admin/labels'
import type { Locale } from '@/i18n/config'

export const metadata: Metadata = { title: 'Customer | ERP Generator Administration', robots: { index: false, follow: false } }

export default async function CustomerPage({
  params,
}: {
  params: Promise<{ locale: Locale; customerId: string }>
}) {
  const { locale, customerId } = await params
  return <AdminCustomerDetailsPage locale={locale} customerId={customerId} labels={getAdminLabels(locale)} />
}
