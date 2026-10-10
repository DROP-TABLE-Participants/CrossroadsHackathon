import { NextRequest } from 'next/server'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { GET, POST } from '@/app/api/[...path]/route'

const portalOrigin = 'https://portal.erpgenerator.example'
const routeContext = (path: string[]) => ({ params: Promise.resolve({ path }) })

function authenticationPayload(accessToken = 'access-secret', refreshToken = 'refresh-secret') {
  const now = Math.floor(Date.now() / 1000)
  return {
    user: { id: 'customer-1', email: 'owner@example.test', name: 'Owner' },
    accessToken,
    accessTokenExpiresAt: now + 900,
    refreshToken,
    refreshTokenExpiresAt: now + 604800,
  }
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
  vi.clearAllMocks()
})

describe('Portal HttpOnly BFF session', () => {
  it('creates a guest with HttpOnly cookies and resumes the same browser identity', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    vi.stubEnv('FRONTEND_URL', portalOrigin)
    const upstream = vi.fn(async (url: string) => url.endsWith('/me') ? Response.json({ user: authenticationPayload().user }) : Response.json(authenticationPayload()))
    vi.stubGlobal('fetch', upstream)
    const request = (cookie = '') => new NextRequest(`${portalOrigin}/api/customers/guest-session`, { method: 'POST', headers: { origin: portalOrigin, cookie, 'content-type': 'application/json' }, body: '{}' })
    const created = await POST(request(), routeContext(['customers', 'guest-session']))
    expect(await created.json()).toEqual({ user: authenticationPayload().user })
    const cookie = created.headers.getSetCookie().join(';')
    expect(cookie).toContain('HttpOnly')
    expect(cookie).not.toContain('Domain=')
    const resumed = await POST(request('erpgenerator-customer-access=access-secret'), routeContext(['customers', 'guest-session']))
    expect(await resumed.json()).toEqual({ user: authenticationPayload().user })
    expect(upstream.mock.calls.filter(([url]) => url.endsWith('/guest-session'))).toHaveLength(1)
  })

  it('preserves a refresh-only workspace during an upstream outage instead of creating a new guest', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    vi.stubEnv('FRONTEND_URL', portalOrigin)
    const upstream = vi.fn(async () => new Response(null, { status: 503 }))
    vi.stubGlobal('fetch', upstream)
    const response = await POST(new NextRequest(`${portalOrigin}/api/customers/guest-session`, { method: 'POST', headers: { origin: portalOrigin, cookie: 'erpgenerator-customer-refresh=old-refresh' }, body: '{}' }), routeContext(['customers', 'guest-session']))
    expect(response.status).toBe(502)
    expect(response.headers.getSetCookie()).toHaveLength(0)
    expect(upstream).toHaveBeenCalledOnce()
  })
  it('stores API tokens only in host-scoped HttpOnly cookies and returns safe login data', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    vi.stubEnv('FRONTEND_URL', portalOrigin)
    const upstreamFetch = vi.fn(async (_url: string, init?: RequestInit) => {
      const headers = new Headers(init?.headers)
      expect(headers.get('authorization')).toBeNull()
      expect(headers.get('x-erpgenerator-portal-secret')).toBe('portal-secret')
      return Response.json(authenticationPayload())
    })
    vi.stubGlobal('fetch', upstreamFetch)

    const request = new NextRequest(`${portalOrigin}/api/customers/login`, {
      method: 'POST',
      headers: {
        origin: portalOrigin,
        authorization: 'JWT browser-controlled-token',
        'content-type': 'application/json',
      },
      body: JSON.stringify({ email: 'owner@example.test', password: 'Secret!123' }),
    })
    const response = await POST(request, routeContext(['customers', 'login']))
    const payload = await response.json()
    const cookies = response.headers.getSetCookie().join(';')

    expect(payload).toEqual({ user: authenticationPayload().user })
    expect(JSON.stringify(payload)).not.toContain('access-secret')
    expect(JSON.stringify(payload)).not.toContain('refresh-secret')
    expect(cookies).toContain('erpgenerator-customer-access=access-secret')
    expect(cookies).toContain('erpgenerator-customer-refresh=refresh-secret')
    expect(cookies).toContain('HttpOnly')
    expect(cookies).toContain('SameSite=strict')
    expect(cookies).not.toContain('Domain=')
  })

  it('rejects state changes from an origin other than the configured public Portal', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    vi.stubEnv('FRONTEND_URL', portalOrigin)
    const upstreamFetch = vi.fn()
    vi.stubGlobal('fetch', upstreamFetch)

    const request = new NextRequest(`${portalOrigin}/api/customers/login`, {
      method: 'POST',
      headers: { origin: 'https://attacker.example', 'content-type': 'application/json' },
      body: JSON.stringify({ email: 'owner@example.test', password: 'Secret!123' }),
    })
    const response = await POST(request, routeContext(['customers', 'login']))

    expect(response.status).toBe(403)
    expect(upstreamFetch).not.toHaveBeenCalled()
  })

  it('ignores browser authorization and injects only the HttpOnly access cookie', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    const upstreamFetch = vi.fn(async (_url: string, init?: RequestInit) => {
      const headers = new Headers(init?.headers)
      expect(headers.get('authorization')).toBe('Bearer cookie-access-token')
      return Response.json({ user: { id: 'customer-1' } })
    })
    vi.stubGlobal('fetch', upstreamFetch)

    const request = new NextRequest(`${portalOrigin}/api/customers/me`, {
      headers: {
        authorization: 'Bearer browser-controlled-token',
        cookie: 'erpgenerator-customer-access=cookie-access-token',
      },
    })
    const response = await GET(request, routeContext(['customers', 'me']))

    expect(response.status).toBe(200)
    expect(upstreamFetch).toHaveBeenCalledOnce()
  })

  it('rotates from the HttpOnly refresh cookie when the access cookie is absent', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    const upstreamFetch = vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/api/customers/refresh')) {
        expect(await new Response(init?.body).json()).toEqual({ refreshToken: 'old-refresh' })
        return Response.json(authenticationPayload('rotated-access', 'rotated-refresh'))
      }
      expect(new Headers(init?.headers).get('authorization')).toBe('Bearer rotated-access')
      return Response.json({ user: { id: 'customer-1' } })
    })
    vi.stubGlobal('fetch', upstreamFetch)

    const request = new NextRequest(`${portalOrigin}/api/customers/me`, {
      headers: { cookie: 'erpgenerator-customer-refresh=old-refresh' },
    })
    const response = await GET(request, routeContext(['customers', 'me']))

    expect(response.status).toBe(200)
    expect(upstreamFetch).toHaveBeenCalledTimes(2)
    expect(response.headers.getSetCookie().join(';')).toContain('rotated-refresh')
  })

  it('uses only administrator cookies for administrator routes', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    const upstreamFetch = vi.fn(async (_url: string, init?: RequestInit) => {
      expect(new Headers(init?.headers).get('authorization')).toBe('Bearer administrator-access')
      return Response.json({ id: 'admin-1', role: 'PlatformAdministrator' })
    })
    vi.stubGlobal('fetch', upstreamFetch)

    const request = new NextRequest(`${portalOrigin}/api/admin-auth/me`, {
      headers: {
        cookie:
          'erpgenerator-customer-access=customer-access; erpgenerator-admin-access=administrator-access',
      },
    })
    const response = await GET(request, routeContext(['admin-auth', 'me']))

    expect(response.status).toBe(200)
    expect(upstreamFetch).toHaveBeenCalledOnce()
  })

  it('never forwards an administrator cookie to a customer route', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    const upstreamFetch = vi.fn(async (_url: string, init?: RequestInit) => {
      expect(new Headers(init?.headers).get('authorization')).toBeNull()
      return new Response(null, { status: 401 })
    })
    vi.stubGlobal('fetch', upstreamFetch)

    const request = new NextRequest(`${portalOrigin}/api/customers/me`, {
      headers: { cookie: 'erpgenerator-admin-access=administrator-access' },
    })
    const response = await GET(request, routeContext(['customers', 'me']))

    expect(response.status).toBe(401)
    expect(upstreamFetch).toHaveBeenCalledOnce()
  })

  it('uses only the administrator cookie for administrator management actions', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    vi.stubEnv('FRONTEND_URL', portalOrigin)
    const upstreamFetch = vi.fn(async (url: string, init?: RequestInit) => {
      expect(url).toBe('http://erpgenerator-api/api/admin/administrators')
      const headers = new Headers(init?.headers)
      expect(headers.get('authorization')).toBe('Bearer administrator-access')
      expect(headers.get('x-erpgenerator-portal-secret')).toBe('portal-secret')
      return Response.json({ id: 'admin-2', email: 'new-admin@example.test' }, { status: 201 })
    })
    vi.stubGlobal('fetch', upstreamFetch)

    const request = new NextRequest(`${portalOrigin}/api/admin/administrators`, {
      method: 'POST',
      headers: {
        origin: portalOrigin,
        'content-type': 'application/json',
        cookie:
          'erpgenerator-customer-access=customer-access; erpgenerator-admin-access=administrator-access',
      },
      body: JSON.stringify({ name: 'New administrator', email: 'new-admin@example.test', locale: 'en' }),
    })
    const response = await POST(request, routeContext(['admin', 'administrators']))

    expect(response.status).toBe(201)
    expect(upstreamFetch).toHaveBeenCalledOnce()
  })

  it('clears only administrator cookies after an administrator password reset', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    vi.stubEnv('FRONTEND_URL', portalOrigin)
    const upstreamFetch = vi.fn(async (_url: string, init?: RequestInit) => {
      expect(new Headers(init?.headers).get('authorization')).toBeNull()
      return new Response(null, { status: 204 })
    })
    vi.stubGlobal('fetch', upstreamFetch)

    const request = new NextRequest(`${portalOrigin}/api/admin-auth/reset-password`, {
      method: 'POST',
      headers: {
        origin: portalOrigin,
        'content-type': 'application/json',
        cookie:
          'erpgenerator-customer-access=customer-access; erpgenerator-customer-refresh=customer-refresh; erpgenerator-admin-access=administrator-access; erpgenerator-admin-refresh=administrator-refresh',
      },
      body: JSON.stringify({
        email: 'admin@example.test',
        token: 'reset-token',
        newPassword: 'New-Administrator-Password-1234',
      }),
    })
    const response = await POST(request, routeContext(['admin-auth', 'reset-password']))
    const cookies = response.headers.getSetCookie().join(';')

    expect(response.status).toBe(204)
    expect(cookies).toContain('erpgenerator-admin-access=')
    expect(cookies).toContain('erpgenerator-admin-refresh=')
    expect(cookies).toContain('Max-Age=0')
    expect(cookies).not.toContain('erpgenerator-customer-access=')
    expect(cookies).not.toContain('erpgenerator-customer-refresh=')
  })

  it('forwards a signed Nango webhook byte-for-byte without requiring a browser origin', async () => {
    vi.stubEnv('ERP_GENERATOR_API_INTERNAL_URL', 'http://erpgenerator-api')
    vi.stubEnv('ERP_GENERATOR_PORTAL_INTERNAL_SECRET', 'portal-secret')
    vi.stubEnv('FRONTEND_URL', portalOrigin)
    const rawBody = '{"type":"sync","connectionId":"connection-1"}'
    const upstreamFetch = vi.fn(async (url: string, init?: RequestInit) => {
      expect(url).toBe('http://erpgenerator-api/api/integrations/nango/webhook')
      const headers = new Headers(init?.headers)
      expect(headers.get('x-nango-hmac-sha256')).toBe('signed-payload')
      expect(headers.get('authorization')).toBeNull()
      expect(await new Response(init?.body).text()).toBe(rawBody)
      return new Response(null, { status: 204 })
    })
    vi.stubGlobal('fetch', upstreamFetch)

    const request = new NextRequest(`${portalOrigin}/api/integrations/nango/webhook`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-nango-hmac-sha256': 'signed-payload',
      },
      body: rawBody,
    })
    const response = await POST(request, routeContext(['integrations', 'nango', 'webhook']))

    expect(response.status).toBe(204)
    expect(upstreamFetch).toHaveBeenCalledOnce()
  })
})
