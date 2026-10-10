'use client'

import { useMemo, type ReactNode } from 'react'
import { Spinner } from '@/components/ui/spinner'
import { Button } from '@/components/ui/button'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { getLabels } from '@/components/account/labels'
import { AccountSidebarShell } from './AccountSidebarShell'
import type { Locale } from '@/i18n/config'

export function AccountGate({ locale, children }: { locale: Locale; children: ReactNode }) {
  const { customer, sessionResolved, sessionError, refreshCustomer } = useDemoBooking()
  const labels = useMemo(() => getLabels(locale), [locale])
  if (!customer) return <div className="flex min-h-svh flex-col items-center justify-center gap-4 bg-background p-6 text-center">
    {sessionResolved && sessionError ? <><p>{locale === 'bg' ? 'Работното пространство не е достъпно в момента. Опитайте отново.' : 'Your workspace is temporarily unavailable. Please try again.'}</p><Button onClick={() => void refreshCustomer()}>{locale === 'bg' ? 'Опитай отново' : 'Try again'}</Button></> : <><Spinner className="size-5" /><p>{locale === 'bg' ? 'Подготвяме работното ви пространство…' : 'Preparing your workspace…'}</p></>}
  </div>
  return <AccountSidebarShell locale={locale} labels={labels}>{children}</AccountSidebarShell>
}
