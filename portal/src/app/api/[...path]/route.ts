import { NextRequest, NextResponse } from 'next/server'

import { getPortalApiRouteRealm } from '@/lib/api-bff/allowlist'
import {
  customerAccessCookieName,
  customerRefreshCookieName,
} from '@/lib/auth/portal-entry'

const maximumRequestBytes = 1_048_576
const forwardedRequestHeaders = ['accept', 'content-type', 'idempotency-key'] as const
const forwardedResponseHeaders = ['content-type', 'cache-control', 'etag', 'retry-after'] as const
const sessionCreationRoutes = new Set([
  'customers/guest-session',
  'customers/login',
  'customers/register',
  'customers/refresh',
])
const unauthenticatedRoutes = new Set([
  ...sessionCreationRoutes,
  'customers/forgot-password',
  'customers/reset-password',
])

type RouteContext = { params: Promise<{ path: string[] }> }
type AuthenticationPayload = {
  user?: unknown
  accessToken?: string
  accessTokenExpiresAt?: number
  refreshToken?: string
  refreshTokenExpiresAt?: number
}

function isUnsafeMethod(method: string) {
  return !['GET', 'HEAD', 'OPTIONS'].includes(method.toUpperCase())
}

function configuredPortalOrigin(request: NextRequest) {
  const configured = process.env.FRONTEND_URL?.trim().replace(/\/$/, '')
  if (!configured) return request.nextUrl.origin
  try {
    const parsed = new URL(configured)
    return ['http:', 'https:'].includes(parsed.protocol) && parsed.origin === configured
      ? parsed.origin
      : null
  } catch {
    return null
  }
}

function clientIp(request: NextRequest) {
  return request.headers.get('x-forwarded-for')?.split(',')[0]?.trim() || 'unknown'
}

function apiHeaders(
  request: NextRequest,
  portalSecret: string,
  accessToken?: string,
  forwardNangoSignature = false,
) {
  const headers = new Headers()
  for (const name of forwardedRequestHeaders) {
    const value = request.headers.get(name)
    if (value) headers.set(name, value)
  }
  headers.set('x-erpgenerator-portal-secret', portalSecret)
  headers.set('x-erpgenerator-client-ip', clientIp(request))
  if (accessToken) headers.set('authorization', `Bearer ${accessToken}`)
  if (forwardNangoSignature) {
    const signature = request.headers.get('x-nango-hmac-sha256')
    if (signature) headers.set('x-nango-hmac-sha256', signature)
  }
  return headers
}

async function callApi(
  apiBaseUrl: string,
  route: string,
  search: string,
  method: string,
  headers: Headers,
  body: ArrayBuffer | undefined,
  signal: AbortSignal,
) {
  return fetch(`${apiBaseUrl}/api/${route}${search}`, {
    method,
    headers,
    body,
    cache: 'no-store',
    redirect: 'manual',
    signal,
  })
}

const sessionCookieNames = { access: customerAccessCookieName, refresh: customerRefreshCookieName }
const refreshRoute = 'customers/refresh'

async function refreshSession(
  request: NextRequest,
  apiBaseUrl: string,
  portalSecret: string,
  refreshToken: string,
) {
  const headers = apiHeaders(request, portalSecret)
  headers.set('content-type', 'application/json')
  const upstream = await callApi(
    apiBaseUrl,
    refreshRoute,
    '',
    'POST',
    headers,
    new TextEncoder().encode(JSON.stringify({ refreshToken })).buffer,
    request.signal,
  )
  if (!upstream.ok) {
    if (upstream.status === 401 || upstream.status === 403) return null
    throw new Error('Session refresh is temporarily unavailable')
  }
  const payload = (await upstream.json()) as AuthenticationPayload
  return validAuthentication(payload) ? payload : null
}

function validAuthentication(payload: AuthenticationPayload) {
  return Boolean(
    payload.user &&
      payload.accessToken &&
      payload.refreshToken &&
      Number.isFinite(payload.accessTokenExpiresAt) &&
      Number.isFinite(payload.refreshTokenExpiresAt),
  )
}

function cookieSecure(request: NextRequest) {
  const origin = configuredPortalOrigin(request)
  const originHost = origin ? new URL(origin).hostname : request.nextUrl.hostname
  const localHost = ['localhost', '127.0.0.1'].includes(originHost)
  if (process.env.ERP_GENERATOR_COOKIE_SECURE === 'false' && localHost) return false
  return configuredPortalOrigin(request)?.startsWith('https://') || process.env.NODE_ENV === 'production'
}

function setSessionCookies(
  response: NextResponse,
  request: NextRequest,
  payload: AuthenticationPayload,
) {
  const now = Math.floor(Date.now() / 1000)
  const names = sessionCookieNames
  response.cookies.set(names.access, payload.accessToken!, {
    httpOnly: true,
    secure: cookieSecure(request),
    sameSite: 'strict',
    path: '/',
    maxAge: Math.max(1, payload.accessTokenExpiresAt! - now),
  })
  response.cookies.set(names.refresh, payload.refreshToken!, {
    httpOnly: true,
    secure: cookieSecure(request),
    sameSite: 'strict',
    path: '/',
    maxAge: Math.max(1, payload.refreshTokenExpiresAt! - now),
  })
}

function clearSessionCookies(response: NextResponse, request: NextRequest) {
  const names = sessionCookieNames
  for (const name of [names.access, names.refresh]) {
    response.cookies.set(name, '', {
      httpOnly: true,
      secure: cookieSecure(request),
      sameSite: 'strict',
      path: '/',
      maxAge: 0,
    })
  }
}

async function safeResponse(upstream: Response) {
  const responseHeaders = new Headers()
  for (const name of forwardedResponseHeaders) {
    const value = upstream.headers.get(name)
    if (value) responseHeaders.set(name, value)
  }
  responseHeaders.set('cache-control', 'no-store')
  return new NextResponse(upstream.body, { status: upstream.status, headers: responseHeaders })
}

async function forward(request: NextRequest, context: RouteContext) {
  const { path } = await context.params
  const realm = getPortalApiRouteRealm(request.method, path)
  if (!realm) {
    return NextResponse.json({ message: 'This portal API route is not available.' }, { status: 404 })
  }

  const route = path.join('/')
  const isNangoWebhook = realm === 'public' && route === 'integrations/nango/webhook' && request.method === 'POST'
  const portalOrigin = configuredPortalOrigin(request)
  if (!isNangoWebhook && !portalOrigin) {
    return NextResponse.json({ message: 'The portal origin is not configured safely.' }, { status: 503 })
  }

  if (
    !isNangoWebhook &&
    isUnsafeMethod(request.method) &&
    request.headers.get('origin') !== portalOrigin
  ) {
    return NextResponse.json({ message: 'Invalid request origin.' }, { status: 403 })
  }

  const apiBaseUrl = process.env.ERP_GENERATOR_API_INTERNAL_URL?.trim().replace(/\/$/, '')
  const portalSecret = process.env.ERP_GENERATOR_PORTAL_INTERNAL_SECRET?.trim()
  if (!apiBaseUrl || !portalSecret) {
    return NextResponse.json({ message: 'The portal API is temporarily unavailable.' }, { status: 503 })
  }

  const contentLength = Number(request.headers.get('content-length') || '0')
  if (Number.isFinite(contentLength) && contentLength > maximumRequestBytes) {
    return NextResponse.json({ message: 'Request body is too large.' }, { status: 413 })
  }

  let body: ArrayBuffer | undefined
  if (!['GET', 'HEAD'].includes(request.method)) {
    body = await request.arrayBuffer()
    if (body.byteLength > maximumRequestBytes) {
      return NextResponse.json({ message: 'Request body is too large.' }, { status: 413 })
    }
  }

  const authenticatedRealm = realm === 'public' ? null : realm
  const names = authenticatedRealm ? sessionCookieNames : null
  const refreshToken = names ? request.cookies.get(names.refresh)?.value : undefined
  let accessToken = names ? request.cookies.get(names.access)?.value : undefined

  try {
    if (isNangoWebhook) {
      const upstream = await callApi(
        apiBaseUrl,
        route,
        request.nextUrl.search,
        request.method,
        apiHeaders(request, portalSecret, undefined, true),
        body,
        request.signal,
      )
      return safeResponse(upstream)
    }

    if (authenticatedRealm && route === 'customers/logout') {
      const headers = apiHeaders(request, portalSecret, accessToken)
      headers.set('content-type', 'application/json')
      const upstream = await callApi(
        apiBaseUrl,
        route,
        request.nextUrl.search,
        'POST',
        headers,
        new TextEncoder().encode(JSON.stringify({ refreshToken })).buffer,
        request.signal,
      )
      const response = await safeResponse(upstream)
      clearSessionCookies(response, request)
      return response
    }

    if (authenticatedRealm && sessionCreationRoutes.has(route)) {
      if (route === 'customers/guest-session' && (accessToken || refreshToken)) {
        let resumed: AuthenticationPayload | null = null
        if (!accessToken && refreshToken) resumed = await refreshSession(request, apiBaseUrl, portalSecret, refreshToken)
        let profile = await callApi(apiBaseUrl, 'customers/me', '', 'GET', apiHeaders(request, portalSecret, resumed?.accessToken || accessToken), undefined, request.signal)
        if (profile.status === 401 && refreshToken && !resumed) {
          resumed = await refreshSession(request, apiBaseUrl, portalSecret, refreshToken)
          if (resumed) profile = await callApi(apiBaseUrl, 'customers/me', '', 'GET', apiHeaders(request, portalSecret, resumed.accessToken), undefined, request.signal)
        }
        if (profile.ok) {
          const response = await safeResponse(profile)
          if (resumed) setSessionCookies(response, request, resumed)
          return response
        }
        if (profile.status !== 401) return safeResponse(profile)
      }
      if (route === refreshRoute && refreshToken) {
        body = new TextEncoder().encode(JSON.stringify({ refreshToken })).buffer
      }
      const upstream = await callApi(
        apiBaseUrl,
        route,
        request.nextUrl.search,
        request.method,
        apiHeaders(request, portalSecret),
        body,
        request.signal,
      )
      if (!upstream.ok) {
        const response = await safeResponse(upstream)
        if (route === refreshRoute) {
          clearSessionCookies(response, request)
        }
        return response
      }
      const payload = (await upstream.json()) as AuthenticationPayload
      if (!validAuthentication(payload)) {
        return NextResponse.json({ message: 'Invalid authentication response.' }, { status: 502 })
      }
      const response = NextResponse.json({ user: payload.user }, { status: upstream.status })
      response.headers.set('cache-control', 'no-store')
      setSessionCookies(response, request, payload)
      return response
    }

    if (realm === 'public' || unauthenticatedRoutes.has(route)) {
      const upstream = await callApi(
        apiBaseUrl,
        route,
        request.nextUrl.search,
        request.method,
        apiHeaders(request, portalSecret),
        body,
        request.signal,
      )
      const response = await safeResponse(upstream)
      if (
        upstream.ok &&
        authenticatedRealm &&
        route === 'customers/reset-password'
      ) {
        clearSessionCookies(response, request)
      }
      return response
    }

    let rotatedSession: AuthenticationPayload | null = null
    if (authenticatedRealm && !accessToken && refreshToken) {
      rotatedSession = await refreshSession(
        request,
        apiBaseUrl,
        portalSecret,
        refreshToken,
      )
      accessToken = rotatedSession?.accessToken
    }

    let upstream = await callApi(
      apiBaseUrl,
      route,
      request.nextUrl.search,
      request.method,
      apiHeaders(request, portalSecret, accessToken),
      body,
      request.signal,
    )
    if (upstream.status === 401 && authenticatedRealm && refreshToken && !rotatedSession) {
      rotatedSession = await refreshSession(
        request,
        apiBaseUrl,
        portalSecret,
        refreshToken,
      )
      if (rotatedSession) {
        upstream = await callApi(
          apiBaseUrl,
          route,
          request.nextUrl.search,
          request.method,
          apiHeaders(request, portalSecret, rotatedSession.accessToken),
          body,
          request.signal,
        )
      }
    }

    const opensRuntime = route === 'customers/runtime/open' || /^customers\/(demos\/[0-9a-f-]{36}|generation-runs\/[0-9a-f-]{36}\/environments\/(staging|production))\/open$/i.test(route)
    let response: NextResponse
    if (upstream.ok && opensRuntime) {
      const entry = await upstream.json() as { ticket: string; entryUrl: string }
      const target = new URL(entry.entryUrl)
      if (!['http:', 'https:'].includes(target.protocol) || target.pathname !== '/api/auth/guest-entry' || !/^[0-9a-f]{64}$/.test(entry.ticket)) throw new Error('Invalid Runtime entry')
      const escape = (value: string) => value.replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]!))
      const nonce = crypto.randomUUID()
      response = new NextResponse(`<!doctype html><html lang="en"><meta name="referrer" content="origin"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Opening ERP</title><body><p>Opening your ERP…</p><form method="post" action="${escape(target.href)}"><input type="hidden" name="ticket" value="${entry.ticket}"><button type="submit">Open ERP</button></form><script nonce="${nonce}">document.querySelector('form').submit()</script></body></html>`, { headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'referrer-policy': 'origin', 'content-security-policy': `default-src 'none'; script-src 'nonce-${nonce}'; form-action ${target.origin}; base-uri 'none'; frame-ancestors 'none'` } })
    } else if (opensRuntime) {
      response = new NextResponse('<!doctype html><html lang="en"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Return to your workspace</title><body><p>This ERP could not be opened from your workspace. Return and try again when it is ready.</p><p>Това ERP не може да бъде отворено. Върнете се в работното си пространство и опитайте отново.</p><a href="/en/account">My ERPs / Моите ERP системи</a></body></html>', { status: upstream.status, headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'content-security-policy': "default-src 'none'; base-uri 'none'; frame-ancestors 'none'" } })
    } else response = await safeResponse(upstream)
    if (rotatedSession && authenticatedRealm) {
      setSessionCookies(response, request, rotatedSession)
    }
    if (upstream.status === 401 && authenticatedRealm) {
      clearSessionCookies(response, request)
    }
    return response
  } catch {
    return NextResponse.json({ message: 'The portal API is temporarily unavailable.' }, { status: 502 })
  }
}

export const GET = forward
export const POST = forward
export const PATCH = forward
export const PUT = forward
export const DELETE = forward
