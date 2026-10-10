import { notFound } from 'next/navigation'

import { AuthLayout } from '@/components/auth/AuthLayout'
import { getAuthCopy } from '@/components/auth/copy'
import { ForgotPasswordForm } from '@/components/auth/PasswordResetForms'
import { locales, type Locale } from '@/i18n/config'

export default async function AdminForgotPasswordPage({ params }: { params: Promise<{ locale: Locale }> }) {
  const { locale } = await params
  if (!locales.includes(locale)) notFound()
  const copy = getAuthCopy(locale)
  return (
    <AuthLayout
      locale={locale}
      title={locale === 'bg' ? 'Администраторска парола' : 'Administrator password'}
      description={locale === 'bg' ? 'Ще изпратим отделен администраторски линк.' : 'We will send a separate administrator reset link.'}
      legal={copy.legal}
      showcase={copy.showcase}
    >
      <ForgotPasswordForm locale={locale} realm="administrator" />
    </AuthLayout>
  )
}
