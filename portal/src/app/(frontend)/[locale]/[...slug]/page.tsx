import { notFound, redirect } from 'next/navigation'

import { locales, type Locale } from '@/i18n/config'
import { getLandingUrl } from '@/lib/landing-url'

export default async function LandingContentRedirect({
  params,
}: {
  params: Promise<{ locale: string; slug: string[] }>
}) {
  const { locale, slug } = await params
  if (!locales.includes(locale as Locale) || !slug.length) notFound()
  redirect(`${getLandingUrl()}/${locale}/${slug.map(encodeURIComponent).join('/')}`)
}
