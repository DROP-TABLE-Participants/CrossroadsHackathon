import type { Metadata } from 'next'

import { AdminGenerationRunDetailsPage } from '@/components/admin/AdminGenerationRunDetailsPage'
import { getAdminLabels } from '@/components/admin/labels'
import type { Locale } from '@/i18n/config'

export const metadata: Metadata = {
  title: 'Generation Run | ERP Generator Administration',
  robots: { index: false, follow: false },
}

export default async function GenerationRunPage({
  params,
}: {
  params: Promise<{ locale: Locale; runId: string }>
}) {
  const { locale, runId } = await params
  return (
    <AdminGenerationRunDetailsPage
      locale={locale}
      runId={runId}
      labels={getAdminLabels(locale)}
    />
  )
}
