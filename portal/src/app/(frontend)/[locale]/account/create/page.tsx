import type { Metadata } from 'next'
import { notFound } from 'next/navigation'

import { CreateErpWizard } from '@/components/account/wizard/CreateErpWizard'
import { locales, type Locale } from '@/i18n/config'
import { toAbsoluteUrl } from '@/lib/site-url'

export default async function CreateErpPage({
  params,
}: {
  params: Promise<{ locale: Locale }>
}) {
  const { locale } = await params

  if (!locales.includes(locale)) notFound()

  return <CreateErpWizard locale={locale} />
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
    title: locale === 'bg' ? 'Създайте вашата ERP | ERP Generator' : 'Create your ERP | ERP Generator',
    robots: {
      index: false,
      follow: false,
    },
    alternates: {
      canonical: toAbsoluteUrl(`/${locale}/account/create`),
      languages: {
        en: toAbsoluteUrl('/en/account/create'),
        bg: toAbsoluteUrl('/bg/account/create'),
      },
    },
  }
}
