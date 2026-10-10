import type { Metadata } from 'next'

import { AdminOrchestrationPage } from '@/components/admin/AdminOrchestrationPage'
import { getAdminLabels } from '@/components/admin/labels'
import type { Locale } from '@/i18n/config'

export const metadata: Metadata = {
  title: 'Orchestration | ERP Generator Administration',
  robots: { index: false, follow: false },
}

export default async function OrchestrationPage({
  params,
}: {
  params: Promise<{ locale: Locale }>
}) {
  const { locale } = await params
  return <AdminOrchestrationPage locale={locale} labels={getAdminLabels(locale)} />
}
