import { describe, expect, it } from 'vitest'

import {
  getPortalApiRouteRealm,
  isAllowedPortalApiRoute,
} from '../../src/lib/api-bff/allowlist'

describe('portal API allowlist', () => {
  it('allows only customer-facing routes and methods', () => {
    expect(isAllowedPortalApiRoute('POST', ['customers', 'login'])).toBe(true)
    expect(isAllowedPortalApiRoute('POST', ['customers', 'refresh'])).toBe(true)
    expect(isAllowedPortalApiRoute('GET', ['customers', 'generation-projects'])).toBe(true)
    expect(isAllowedPortalApiRoute('DELETE', ['customers', 'generation-projects', 'd70f70ae-1373-46b7-b840-68356d011249'])).toBe(true)
    expect(isAllowedPortalApiRoute('POST', ['customers', 'generation-runs', 'd70f70ae-1373-46b7-b840-68356d011249', 'open'])).toBe(true)
    expect(isAllowedPortalApiRoute('POST', ['customers', 'generation-projects', 'd70f70ae-1373-46b7-b840-68356d011249', 'input-sources', 'reconcile'])).toBe(true)
    expect(isAllowedPortalApiRoute('GET', ['customers', 'erp-feedback'])).toBe(true)
    const invite = 'Abcdefghijklmnopqrstuvwxyz123456'
    expect(isAllowedPortalApiRoute('POST', ['interviews', invite, 'messages'])).toBe(true)
    expect(isAllowedPortalApiRoute('POST', ['interviews', invite, 'assistant-response'])).toBe(true)
    expect(isAllowedPortalApiRoute('POST', ['interviews', invite, 'voice-token'])).toBe(true)
  })

  it('denies removed Runtime environment and promotion routes', () => {
    const run = ['customers', 'generation-runs', 'd70f70ae-1373-46b7-b840-68356d011249']
    expect(isAllowedPortalApiRoute('POST', [...run, 'promote-production'])).toBe(false)
    for (const environment of ['staging', 'production']) {
      expect(isAllowedPortalApiRoute('POST', [...run, 'environments', environment, 'open'])).toBe(false)
    }
  })

  it('allows only POST for the signed Nango webhook ingress', () => {
    expect(isAllowedPortalApiRoute('POST', ['integrations', 'nango', 'webhook'])).toBe(true)
    expect(isAllowedPortalApiRoute('GET', ['integrations', 'nango', 'webhook'])).toBe(false)
    expect(isAllowedPortalApiRoute('POST', ['integrations', 'nango', 'webhook', 'other'])).toBe(false)
  })

  it('assigns every allowed route to exactly one authentication realm', () => {
    expect(getPortalApiRouteRealm('GET', ['customers', 'me'])).toBe('customer')
    expect(getPortalApiRouteRealm('POST', ['customers', 'forgot-password'])).toBe('customer')
    expect(getPortalApiRouteRealm('GET', ['demo-booking', 'availability'])).toBe('public')
    expect(getPortalApiRouteRealm('GET', ['unknown'])).toBeNull()
  })

  it('denies internal and operator routes even when the API implements them', () => {
    expect(isAllowedPortalApiRoute('POST', ['admin-auth', 'login'])).toBe(false)
    expect(isAllowedPortalApiRoute('GET', ['admin', 'overview'])).toBe(false)
    expect(isAllowedPortalApiRoute('POST', ['admin', 'administrators'])).toBe(false)
    expect(isAllowedPortalApiRoute('GET', ['internal', 'runtime', 'tenants', 'resolve'])).toBe(false)
    expect(isAllowedPortalApiRoute('POST', ['operators', 'generation-runs', 'abc', 'resume'])).toBe(false)
    expect(isAllowedPortalApiRoute('GET', ['customers', 'login'])).toBe(false)
  })

  it('rejects path traversal segments', () => {
    expect(isAllowedPortalApiRoute('GET', ['customers', '..', 'internal'])).toBe(false)
    expect(isAllowedPortalApiRoute('GET', ['customers/internal'])).toBe(false)
  })
})
