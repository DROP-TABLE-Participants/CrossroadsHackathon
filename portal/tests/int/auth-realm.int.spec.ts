import { describe, expect, it } from 'vitest'

import { sanitizeNextPath } from '@/components/auth/auth-client'

describe('customer authentication realm routing', () => {
  it('accepts only customer account destinations', () => {
    expect(sanitizeNextPath('/en/account/profile', 'en')).toBe('/en/account/profile')
    expect(sanitizeNextPath('/en/admin', 'en')).toBe('/en/account')
    expect(sanitizeNextPath('/bg/admin/login', 'bg')).toBe('/bg/account')
    expect(sanitizeNextPath('https://attacker.example', 'en')).toBe('/en/account')
  })
})
