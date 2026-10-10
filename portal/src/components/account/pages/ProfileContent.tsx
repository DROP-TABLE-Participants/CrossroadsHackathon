'use client'

import { useEffect, useMemo } from 'react'

import { Card, CardContent } from '@/components/ui/card'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { useProfile } from '@/components/account/hooks/useProfile'
import { getLabels } from '@/components/account/labels'
import { ProfileCard } from '@/components/account/profile/ProfileCard'
import type { Locale } from '@/i18n/config'

/** /account/profile — account settings as a dedicated page. */
export function ProfileContent({ locale }: { locale: Locale }) {
  const { customer, openLogin, refreshCustomer } = useDemoBooking()
  const labels = useMemo(() => getLabels(locale), [locale])

  const profile = useProfile({ labels, openLogin, refreshCustomer })
  const { setName, setEmail, setPhoneNumber, setCompanyName, setPosition } = profile

  useEffect(() => {
    if (!customer) return

    setName(customer.name || '')
    setEmail(customer.email || '')
    setPhoneNumber(customer.phoneNumber || '')
    setCompanyName(customer.companyName || '')
    setPosition(customer.position || '')
  }, [customer, setCompanyName, setEmail, setName, setPhoneNumber, setPosition])

  return (
    <section aria-label={labels.profileTitle} className="flex flex-col gap-4">
      <div className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold">{labels.profileTitle}</h1>
        <p className="text-sm text-muted-foreground">{labels.accountSettings}</p>
      </div>

      <Card className="max-w-2xl">
        <CardContent className="pt-6">
          <ProfileCard labels={labels} profile={profile} />
        </CardContent>
      </Card>
    </section>
  )
}
