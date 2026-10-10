'use client'

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import { usePathname, useRouter } from 'next/navigation'
import { clearAuthSession, normalizeCustomer, saveAuthSession, type CustomerSession, type ActiveDemoBooking } from './customer-session'

type GuestContext = {
  customer: CustomerSession | null
  sessionResolved: boolean
  sessionError: boolean
  activeBooking: ActiveDemoBooking | null
  refreshCustomer: () => Promise<void>
  openLogin: () => void
  openBooking: (options?: { prefillEmail?: string }) => void
  logout: () => Promise<void>
}
const BookingContext = createContext<GuestContext | null>(null)
let bootstrap: Promise<CustomerSession> | null = null

async function resolveGuest() {
  if (!bootstrap) {
    const create = async () => {
      const response = await fetch('/api/customers/guest-session', { method: 'POST', credentials: 'same-origin', headers: { 'content-type': 'application/json' }, body: '{}' })
      if (!response.ok) throw new Error('Guest workspace unavailable')
      const payload = await response.json()
      const customer = normalizeCustomer(payload.user ?? payload)
      if (!customer) throw new Error('Invalid guest session')
      saveAuthSession(customer)
      return customer
    }
    // Browser tabs share cookies. A browser lock prevents two cold tabs from creating competing workspaces.
    bootstrap = (navigator.locks ? navigator.locks.request('erp-generator-guest', create) : create()).finally(() => { bootstrap = null })
  }
  return bootstrap
}

export function DemoBookingProvider({ children }: { children: React.ReactNode; config?: Record<string, string> }) {
  const [customer, setCustomer] = useState<CustomerSession | null>(null)
  const [sessionResolved, setResolved] = useState(false)
  const [sessionError, setError] = useState(false)
  const router = useRouter()
  const pathname = usePathname()
  const locale = pathname?.split('/')[1] === 'bg' ? 'bg' : 'en'
  const refreshCustomer = useCallback(async () => {
    setResolved(false)
    setError(false)
    try { setCustomer(await resolveGuest()) }
    catch { setError(true) }
    finally { setResolved(true) }
  }, [])
  useEffect(() => {
    if (!pathname?.includes('/admin')) void refreshCustomer()
  }, [refreshCustomer, pathname?.includes('/admin')])
  const value = useMemo<GuestContext>(() => ({
    customer, sessionResolved, sessionError, activeBooking: null, refreshCustomer,
    openLogin: () => { void refreshCustomer() },
    openBooking: () => router.push(`/${locale}/account/help`),
    logout: async () => { clearAuthSession(); await refreshCustomer() },
  }), [customer, sessionResolved, sessionError, refreshCustomer, router, locale])
  return <BookingContext.Provider value={value}>{children}</BookingContext.Provider>
}

export function useDemoBooking() {
  const context = useContext(BookingContext)
  if (!context) throw new Error('Guest session provider is missing')
  return context
}
