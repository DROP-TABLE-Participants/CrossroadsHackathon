import type { Metadata } from 'next'
import { notFound } from 'next/navigation'
import { Suspense } from 'react'

import { AdminLoginForm } from '@/components/admin/AdminLoginForm'
import { AuthLayout } from '@/components/auth/AuthLayout'
import { getAuthCopy } from '@/components/auth/copy'
import { locales, type Locale } from '@/i18n/config'

export const metadata: Metadata = {
  title: 'Administrator sign in | ERP Generator',
  robots: { index: false, follow: false },
}

export default async function AdminLoginPage({ params }: { params: Promise<{ locale: Locale }> }) {
  const { locale } = await params
  if (!locales.includes(locale)) notFound()
  const copy = getAuthCopy(locale)
  return (
    <AuthLayout
      locale={locale}
      title={locale === 'bg' ? 'Администрация на ERP Generator' : 'ERP Generator administration'}
      description={
        locale === 'bg'
          ? 'Този вход е само за оторизирани администратори на платформата.'
          : 'This sign-in is only for authorized platform administrators.'
      }
      legal={copy.legal}
      showcase={copy.showcase}
    >
      <Suspense><AdminLoginForm locale={locale} /></Suspense>
    </AuthLayout>
  )
}
