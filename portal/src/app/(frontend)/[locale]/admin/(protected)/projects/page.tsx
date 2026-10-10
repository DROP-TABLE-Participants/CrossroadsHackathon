import type { Metadata } from 'next'

import { getAdminLabels } from '@/components/admin/labels'
import { AdminProjectsPage } from '@/components/admin/AdminProjectsPage'
import type { Locale } from '@/i18n/config'

export const metadata: Metadata = { title: 'ERP Projects | ERP Generator Administration', robots: { index: false, follow: false } }

export default async function ProjectsPage({ params }: { params: Promise<{ locale: Locale }> }) {
  const { locale } = await params
  return <AdminProjectsPage locale={locale} labels={getAdminLabels(locale)} />
}
