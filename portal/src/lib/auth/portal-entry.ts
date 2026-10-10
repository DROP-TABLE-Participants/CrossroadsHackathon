import type { Locale } from '@/i18n/config'

export const customerAccessCookieName = 'erpgenerator-customer-access'
export const customerRefreshCookieName = 'erpgenerator-customer-refresh'
export const adminAccessCookieName = 'erpgenerator-admin-access'
export const adminRefreshCookieName = 'erpgenerator-admin-refresh'

type CookieLookup = {
  has(name: string): boolean
}

export function hasPortalSession(cookieStore: CookieLookup) {
  return cookieStore.has(customerAccessCookieName) || cookieStore.has(customerRefreshCookieName)
}

export function hasAdminSession(cookieStore: CookieLookup) {
  return cookieStore.has(adminAccessCookieName) || cookieStore.has(adminRefreshCookieName)
}

export function getPortalEntryPath(locale: Locale, hasSession: boolean) {
  void hasSession
  return `/${locale}/account`
}
