'use client'

import { useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { AccountHome } from '@/components/account/AccountHome'
import type { Locale } from '@/i18n/config'

export {
  discoveryJourneyStep,
  prioritizeCustomerPendingWorkItems,
  resolveGeneratedBundleSelection,
  shouldShowCustomerArtifact,
  shouldShowRuntimeEnvironmentCreateForm,
} from '@/components/account/helpers'

export function CustomerAccountPage({ locale }: { locale: Locale }) {
  const { customer } = useDemoBooking()
  // Signed-out visitors never see this component in the app (AccountGate
  // redirects to /login); render nothing rather than an interstitial.
  if (!customer) {
    return null
  }

  return <AccountHome locale={locale} />
}
