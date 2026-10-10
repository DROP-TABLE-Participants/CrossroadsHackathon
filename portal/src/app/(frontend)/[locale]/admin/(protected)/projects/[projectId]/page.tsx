import type { Metadata } from 'next'

import { getAdminLabels } from '@/components/admin/labels'
import { AdminProjectDetailsPage } from '@/components/admin/AdminProjectDetailsPage'
import type { Locale } from '@/i18n/config'

export const metadata: Metadata = { title: 'ERP Project | ERP Generator Administration', robots: { index: false, follow: false } }

export default async function ProjectPage({
  params,
}: {
  params: Promise<{ locale: Locale; projectId: string }>
}) {
  const { locale, projectId } = await params
  return <AdminProjectDetailsPage locale={locale} projectId={projectId} labels={getAdminLabels(locale)} />
}
