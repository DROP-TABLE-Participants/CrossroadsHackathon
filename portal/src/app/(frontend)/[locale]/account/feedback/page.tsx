import type { Metadata } from 'next'
import { notFound } from 'next/navigation'

import { FeedbackContent } from '@/components/account/pages/FeedbackContent'
import { locales, type Locale } from '@/i18n/config'
import { toAbsoluteUrl } from '@/lib/site-url'

export default async function AccountFeedbackPage({
  params,
}: {
  params: Promise<{ locale: Locale }>
}) {
  const { locale } = await params

  if (!locales.includes(locale)) notFound()

  return <FeedbackContent locale={locale} />
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
    title:
      locale === 'bg'
        ? 'Обратна връзка и подобрения | ERP Generator'
        : 'Feedback & improvements | ERP Generator',
    robots: {
      index: false,
      follow: false,
    },
    alternates: {
      canonical: toAbsoluteUrl(`/${locale}/account/feedback`),
      languages: {
        en: toAbsoluteUrl('/en/account/feedback'),
        bg: toAbsoluteUrl('/bg/account/feedback'),
      },
    },
  }
}
