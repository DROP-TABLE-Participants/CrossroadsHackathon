'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'

import {
  clearAuthSession,
  shouldClearAuthSessionForCustomerProfileStatus,
} from '@/components/booking/customer-session'
import type { Locale } from '@/i18n/config'

/** Customer authentication may return only to the customer account realm. */
export function sanitizeNextPath(raw: string | null, locale: Locale): string {
  const fallback = `/${locale}/account`
  if (!raw) return fallback
  if (!raw.startsWith('/') || raw.startsWith('//') || raw.includes('\\')) return fallback
  return raw === fallback || raw.startsWith(`${fallback}/`) ? raw : fallback
}

/**
 * Sends already-signed-in visitors of /login and /register straight to their
 * destination. Resolves to false once the visitor is confirmed signed out.
 */
export function useRedirectIfAuthenticated(target: string): boolean {
  const router = useRouter()
  const [checking, setChecking] = useState(true)

  useEffect(() => {
    let cancelled = false
    const verify = async () => {
      try {
        const response = await fetch('/api/customers/me')
        if (cancelled) return
        if (response.ok) {
          router.replace(target)
          return
        }
        if (shouldClearAuthSessionForCustomerProfileStatus(response.status)) {
          clearAuthSession()
        }
        setChecking(false)
      } catch {
        if (!cancelled) setChecking(false)
      }
    }

    void verify()

    return () => {
      cancelled = true
    }
  }, [router, target])

  return checking
}
