import type { Locale } from '@/i18n/config'

export const customerAccessCookieName = 'erpgenerator-customer-access'
export const customerRefreshCookieName = 'erpgenerator-customer-refresh'

type CookieLookup = {
  has(name: string): boolean
}

export function hasPortalSession(cookieStore: CookieLookup) {
  return cookieStore.has(customerAccessCookieName) || cookieStore.has(customerRefreshCookieName)
}

export function getPortalEntryPath(locale: Locale, hasSession: boolean) {
  void hasSession
  return `/${locale}/account`
}
