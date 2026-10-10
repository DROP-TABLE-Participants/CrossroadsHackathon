import type { Metadata } from 'next'

import { AdminAdministratorsPage } from '@/components/admin/AdminAdministratorsPage'
import { getAdminLabels } from '@/components/admin/labels'
import type { Locale } from '@/i18n/config'

export const metadata: Metadata = { title: 'Administrators | ERP Generator Administration', robots: { index: false, follow: false } }

export default async function AdministratorsPage({ params }: { params: Promise<{ locale: Locale }> }) {
  const { locale } = await params
  return <AdminAdministratorsPage locale={locale} labels={getAdminLabels(locale)} />
}
