import { describe, expect, it } from 'vitest'

import {
  adminAccessCookieName,
  customerAccessCookieName,
  customerRefreshCookieName,
  getPortalEntryPath,
  hasAdminSession,
  hasPortalSession,
} from '@/lib/auth/portal-entry'

describe('Portal locale entry routing', () => {
  it.each([
    ['en', false, '/en/account'],
    ['en', true, '/en/account'],
    ['bg', false, '/bg/account'],
    ['bg', true, '/bg/account'],
  ] as const)('routes /%s by session state', (locale, hasSession, expected) => {
    expect(getPortalEntryPath(locale, hasSession)).toBe(expected)
  })

  it('recognizes either HttpOnly session cookie', () => {
    const cookieStore = (names: string[]) => ({
      has: (name: string) => names.includes(name),
    })

    expect(hasPortalSession(cookieStore([]))).toBe(false)
    expect(hasPortalSession(cookieStore([customerAccessCookieName]))).toBe(true)
    expect(hasPortalSession(cookieStore([customerRefreshCookieName]))).toBe(true)
    expect(hasPortalSession(cookieStore([adminAccessCookieName]))).toBe(false)
    expect(hasAdminSession(cookieStore([adminAccessCookieName]))).toBe(true)
  })
})
