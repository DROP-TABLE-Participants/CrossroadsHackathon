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
    expect(isAllowedPortalApiRoute('POST', ['customers', 'generation-runs', 'd70f70ae-1373-46b7-b840-68356d011249', 'promote-production'])).toBe(true)
    expect(isAllowedPortalApiRoute('POST', ['customers', 'generation-projects', 'd70f70ae-1373-46b7-b840-68356d011249', 'input-sources', 'reconcile'])).toBe(true)
    expect(isAllowedPortalApiRoute('GET', ['customers', 'erp-feedback'])).toBe(true)
    const invite = 'Abcdefghijklmnopqrstuvwxyz123456'
    expect(isAllowedPortalApiRoute('POST', ['interviews', invite, 'messages'])).toBe(true)
    expect(isAllowedPortalApiRoute('POST', ['interviews', invite, 'assistant-response'])).toBe(true)
    expect(isAllowedPortalApiRoute('POST', ['interviews', invite, 'voice-token'])).toBe(true)
  })

  it('allows only POST for the signed Nango webhook ingress', () => {
    expect(isAllowedPortalApiRoute('POST', ['integrations', 'nango', 'webhook'])).toBe(true)
    expect(isAllowedPortalApiRoute('GET', ['integrations', 'nango', 'webhook'])).toBe(false)
    expect(isAllowedPortalApiRoute('POST', ['integrations', 'nango', 'webhook', 'other'])).toBe(false)
  })

  it('assigns every allowed route to exactly one authentication realm', () => {
    expect(getPortalApiRouteRealm('GET', ['customers', 'me'])).toBe('customer')
    expect(getPortalApiRouteRealm('POST', ['customers', 'forgot-password'])).toBe('customer')
    expect(getPortalApiRouteRealm('POST', ['admin-auth', 'login'])).toBe('administrator')
    expect(getPortalApiRouteRealm('GET', ['admin', 'customers'])).toBe('administrator')
    expect(getPortalApiRouteRealm('GET', ['admin', 'overview'])).toBe('administrator')
    expect(getPortalApiRouteRealm('GET', ['admin', 'orchestration', 'overview'])).toBe('administrator')
    expect(getPortalApiRouteRealm('GET', ['admin', 'orchestration', 'runs'])).toBe('administrator')
    expect(getPortalApiRouteRealm('GET', ['admin', 'orchestration', 'runs', 'd70f70ae-1373-46b7-b840-68356d011249'])).toBe('administrator')
    expect(getPortalApiRouteRealm('POST', ['admin', 'orchestration', 'runs', 'd70f70ae-1373-46b7-b840-68356d011249', 'retry'])).toBe('administrator')
    expect(getPortalApiRouteRealm('POST', ['admin', 'orchestration', 'runs', 'd70f70ae-1373-46b7-b840-68356d011249', 'cancel'])).toBe('administrator')
    expect(getPortalApiRouteRealm('POST', ['admin', 'orchestration', 'runs', 'd70f70ae-1373-46b7-b840-68356d011249', 'resume'])).toBe('administrator')
    expect(getPortalApiRouteRealm('POST', ['admin', 'administrators'])).toBe('administrator')
    expect(getPortalApiRouteRealm('GET', ['admin', 'audit'])).toBe('administrator')
    expect(getPortalApiRouteRealm('GET', ['admin', 'customers', 'd70f70ae-1373-46b7-b840-68356d011249'])).toBe('administrator')
    expect(getPortalApiRouteRealm('POST', ['admin', 'customers', 'd70f70ae-1373-46b7-b840-68356d011249', 'password-reset'])).toBe('administrator')
    expect(getPortalApiRouteRealm('PATCH', ['admin', 'administrators', 'd70f70ae-1373-46b7-b840-68356d011249', 'status'])).toBe('administrator')
    expect(getPortalApiRouteRealm('GET', ['demo-booking', 'availability'])).toBe('public')
    expect(getPortalApiRouteRealm('GET', ['unknown'])).toBeNull()
  })

  it('denies internal and operator routes even when the API implements them', () => {
    expect(isAllowedPortalApiRoute('GET', ['internal', 'runtime', 'tenants', 'resolve'])).toBe(false)
    expect(isAllowedPortalApiRoute('POST', ['operators', 'generation-runs', 'abc', 'resume'])).toBe(false)
    expect(isAllowedPortalApiRoute('GET', ['customers', 'login'])).toBe(false)
  })

  it('rejects path traversal segments', () => {
    expect(isAllowedPortalApiRoute('GET', ['customers', '..', 'internal'])).toBe(false)
    expect(isAllowedPortalApiRoute('GET', ['customers/internal'])).toBe(false)
  })
})
