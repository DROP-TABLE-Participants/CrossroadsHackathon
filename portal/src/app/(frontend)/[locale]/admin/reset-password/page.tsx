import { notFound } from 'next/navigation'
import { Suspense } from 'react'

import { AuthLayout } from '@/components/auth/AuthLayout'
import { getAuthCopy } from '@/components/auth/copy'
import { ResetPasswordForm } from '@/components/auth/PasswordResetForms'
import { locales, type Locale } from '@/i18n/config'

export default async function AdminResetPasswordPage({ params }: { params: Promise<{ locale: Locale }> }) {
  const { locale } = await params
  if (!locales.includes(locale)) notFound()
  const copy = getAuthCopy(locale)
  return (
    <AuthLayout
      locale={locale}
      title={locale === 'bg' ? 'Нова администраторска парола' : 'New administrator password'}
      description={locale === 'bg' ? 'Създайте нова сигурна администраторска парола.' : 'Create a new secure administrator password.'}
      legal={copy.legal}
      showcase={copy.showcase}
    >
      <Suspense><ResetPasswordForm locale={locale} realm="administrator" /></Suspense>
    </AuthLayout>
  )
}
