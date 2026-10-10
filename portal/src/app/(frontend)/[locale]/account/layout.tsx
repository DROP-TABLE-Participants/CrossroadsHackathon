import { notFound } from 'next/navigation'
import type { ReactNode } from 'react'

import { DemoBookingProvider } from '@/components/booking/DemoBookingProvider'
import { AccountGate } from '@/components/account/shell/AccountGate'
import { locales, type Locale } from '@/i18n/config'

function getBookingConfig(locale: Locale) {
  if (locale === 'bg') {
    return {
      loginTitle: 'Влезте, за да продължите',
      loginDescription: 'Използвайте акаунта си, за да управлявате профила и демо заявката.',
      loginSubmitLabel: 'Вход',
      switchToRegisterLabel: 'Създайте акаунт',
      registerTitle: 'Създайте акаунт',
      registerDescription: 'Създайте акаунт, за да запазите и управлявате демо заявката.',
      registerSubmitLabel: 'Създай акаунт',
      switchToLoginLabel: 'Обратно към вход',
      bookingTitle: 'Запазете демо',
      bookingDescription: 'Изберете начална дата. Автоматично ще бъде резервиран 14-дневен демо период.',
      calendarLabel: 'Начална дата',
      submitBookingLabel: 'Изпрати заявка',
      setupLaterLabel: 'Ще настроя демото по-късно',
      successMessage: 'Представител ще се свърже с вас в рамките на 2-3 работни дни.',
      closeLabel: 'Затвори',
    }
  }

  return {
    loginTitle: 'Login to continue',
    loginDescription: 'Use your account to manage your profile and demo request.',
    loginSubmitLabel: 'Login',
    switchToRegisterLabel: 'Create account',
    registerTitle: 'Create account',
    registerDescription: 'Create an account to book and manage your demo request.',
    registerSubmitLabel: 'Create account',
    switchToLoginLabel: 'Back to login',
    bookingTitle: 'Book your demo',
    bookingDescription: 'Select a start date. A 14-day demo slot will be reserved automatically.',
    calendarLabel: 'Start date',
    submitBookingLabel: 'Submit booking',
    setupLaterLabel: 'Set up demo later',
    successMessage: 'A representative should contact you in 2-3 business days.',
    closeLabel: 'Close',
  }
}

export default async function AccountLayout({
  params,
  children,
}: {
  params: Promise<{ locale: string }>
  children: ReactNode
}) {
  const { locale } = await params

  if (!locales.includes(locale as Locale)) notFound()
  const supportedLocale = locale as Locale

  return (
    <DemoBookingProvider config={getBookingConfig(supportedLocale)}>
      <AccountGate locale={supportedLocale}>{children}</AccountGate>
    </DemoBookingProvider>
  )
}
