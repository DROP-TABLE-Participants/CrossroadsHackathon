import type { Metadata } from 'next'
import { notFound } from 'next/navigation'

import { DashboardContent } from '@/components/account/pages/DashboardContent'
import { locales, type Locale } from '@/i18n/config'
import { toAbsoluteUrl } from '@/lib/site-url'

export default async function AccountPage({
  params,
}: {
  params: Promise<{ locale: Locale }>
}) {
  const { locale } = await params

  if (!locales.includes(locale)) notFound()

  return <DashboardContent locale={locale} />
}

export async function generateStaticParams() {
  return locales.map((locale) => ({ locale }))
}

export async function generateMetadata({
  params,
}: {
  params: Promise<{ locale: Locale }>
}): Promise<Metadata> {
  const { locale } = await params

  return {
    title: locale === 'bg' ? 'Акаунт | ERP Generator' : 'Account | ERP Generator',
    robots: {
      index: false,
      follow: false,
    },
    alternates: {
      canonical: toAbsoluteUrl(`/${locale}/account`),
      languages: {
        en: toAbsoluteUrl('/en/account'),
        bg: toAbsoluteUrl('/bg/account'),
      },
    },
  }
}
