export type PortalApiRealm = 'customer' | 'administrator' | 'public'

const customerExactRoutes = new Map<string, ReadonlySet<string>>([
  ['customers/guest-session', new Set(['POST'])],
  ['customers/demo-catalog', new Set(['GET'])],
  ['customers/demos', new Set(['GET', 'POST'])],
  ['customers/generation-projects/create-from-template', new Set(['POST'])],
  ['customers/runtime/open', new Set(['POST'])],
  ['customers/register', new Set(['POST'])],
  ['customers/login', new Set(['POST'])],
  ['customers/refresh', new Set(['POST'])],
  ['customers/logout', new Set(['POST'])],
  ['customers/forgot-password', new Set(['POST'])],
  ['customers/reset-password', new Set(['POST'])],
  ['customers/me', new Set(['GET', 'PATCH'])],
  ['customers/tenants', new Set(['GET', 'POST'])],
  ['customers/generation-projects', new Set(['GET', 'POST'])],
  ['customers/erp-feedback', new Set(['GET'])],
  ['customers/erp-usage-observations', new Set(['GET'])],
  ['customers/erp-change-proposals', new Set(['GET'])],
  ['customers/erp-change-jobs', new Set(['GET'])],
  ['demo-bookings', new Set(['POST'])],
])

const administratorExactRoutes = new Map<string, ReadonlySet<string>>([
  ['admin-auth/login', new Set(['POST'])],
  ['admin-auth/refresh', new Set(['POST'])],
  ['admin-auth/logout', new Set(['POST'])],
  ['admin-auth/forgot-password', new Set(['POST'])],
  ['admin-auth/reset-password', new Set(['POST'])],
  ['admin-auth/me', new Set(['GET'])],
  ['admin/overview', new Set(['GET'])],
  ['admin/orchestration/overview', new Set(['GET'])],
  ['admin/orchestration/runs', new Set(['GET'])],
  ['admin/customers', new Set(['GET'])],
  ['admin/administrators', new Set(['GET', 'POST'])],
  ['admin/demo-bookings', new Set(['GET'])],
  ['admin/generation-projects', new Set(['GET'])],
  ['admin/audit', new Set(['GET'])],
])

const publicExactRoutes = new Map<string, ReadonlySet<string>>([
  ['demo-booking/availability', new Set(['GET'])],
  ['integrations/nango/webhook', new Set(['POST'])],
  ['health', new Set(['GET'])],
  ['ready', new Set(['GET'])],
])

const customerDynamicRoutes: ReadonlyArray<{ pattern: RegExp; methods: ReadonlySet<string> }> = [
  { pattern: /^customers\/demos\/[0-9a-f-]{36}\/(reset|open)$/i, methods: new Set(['POST']) },
  { pattern: /^customers\/generation-runs\/[0-9a-f-]{36}\/environments\/(staging|production)\/open$/i, methods: new Set(['POST']) },
  { pattern: /^customers\/generation-projects\/[0-9a-f-]{36}$/i, methods: new Set(['GET', 'DELETE']) },
  { pattern: /^customers\/generation-projects\/[0-9a-f-]{36}\/(start-generation|generate-with-assumptions)$/i, methods: new Set(['POST']) },
  { pattern: /^customers\/generation-projects\/[0-9a-f-]{36}\/(nango\/connect-session|input-sources\/reconcile)$/i, methods: new Set(['POST']) },
  { pattern: /^customers\/generation-projects\/[0-9a-f-]{36}\/input-sources\/[0-9a-f-]{36}$/i, methods: new Set(['DELETE']) },
  { pattern: /^customers\/generation-projects\/[0-9a-f-]{36}\/input-sources\/[0-9a-f-]{36}\/resources$/i, methods: new Set(['GET']) },
  { pattern: /^customers\/generation-projects\/[0-9a-f-]{36}\/input-sources\/[0-9a-f-]{36}\/selection$/i, methods: new Set(['PUT']) },
  { pattern: /^customers\/generation-projects\/[0-9a-f-]{36}\/input-sources\/[0-9a-f-]{36}\/refresh$/i, methods: new Set(['POST']) },
  { pattern: /^customers\/generation-runs\/[0-9a-f-]{36}\/promote-production$/i, methods: new Set(['POST']) },
  { pattern: /^customers\/generation-runs\/[0-9a-f-]{36}\/environments\/(staging|production)\/activation-link$/i, methods: new Set(['POST']) },
  { pattern: /^customers\/erp-usage-observations\/[0-9a-f-]{36}\/dismiss$/i, methods: new Set(['POST']) },
  { pattern: /^customers\/pending-work-items\/[0-9a-f-]{36}\/(add-evidence-document|resolve|create-interview-request)$/i, methods: new Set(['POST']) },
  { pattern: /^customers\/interview-requests\/[0-9a-f-]{36}\/(copy-link|send-invite)$/i, methods: new Set(['POST']) },
]

const administratorDynamicRoutes: ReadonlyArray<{ pattern: RegExp; methods: ReadonlySet<string> }> = [
  { pattern: /^admin\/customers\/[0-9a-f-]{36}$/i, methods: new Set(['GET']) },
  { pattern: /^admin\/customers\/[0-9a-f-]{36}\/status$/i, methods: new Set(['PATCH']) },
  { pattern: /^admin\/customers\/[0-9a-f-]{36}\/password-reset$/i, methods: new Set(['POST']) },
  { pattern: /^admin\/generation-projects\/[0-9a-f-]{36}\/diagnostics$/i, methods: new Set(['GET']) },
  { pattern: /^admin\/orchestration\/runs\/[0-9a-f-]{36}$/i, methods: new Set(['GET']) },
  { pattern: /^admin\/orchestration\/runs\/[0-9a-f-]{36}\/(retry|cancel|resume)$/i, methods: new Set(['POST']) },
  { pattern: /^admin\/administrators\/[0-9a-f-]{36}\/status$/i, methods: new Set(['PATCH']) },
  { pattern: /^admin\/administrators\/[0-9a-f-]{36}\/password-reset$/i, methods: new Set(['POST']) },
  { pattern: /^admin\/notification-deliveries\/[0-9a-f-]{36}\/resend$/i, methods: new Set(['POST']) },
]

const publicDynamicRoutes: ReadonlyArray<{ pattern: RegExp; methods: ReadonlySet<string> }> = [
  { pattern: /^interviews\/[A-Za-z0-9_-]{20,200}$/i, methods: new Set(['GET']) },
  { pattern: /^interviews\/[A-Za-z0-9_-]{20,200}\/(start|answers|complete|messages|assistant-response|voice-token)$/i, methods: new Set(['POST']) },
]

function validSegments(segments: string[]) {
  return Boolean(
    segments.length &&
      !segments.some((segment) => !segment || segment === '.' || segment === '..' || /[\\/]/.test(segment)),
  )
}

function matches(
  method: string,
  route: string,
  exact: ReadonlyMap<string, ReadonlySet<string>>,
  dynamic: ReadonlyArray<{ pattern: RegExp; methods: ReadonlySet<string> }>,
) {
  const normalizedMethod = method.toUpperCase()
  if (exact.get(route)?.has(normalizedMethod)) return true
  return dynamic.some(({ pattern, methods }) => methods.has(normalizedMethod) && pattern.test(route))
}

export function getPortalApiRouteRealm(method: string, segments: string[]): PortalApiRealm | null {
  if (!validSegments(segments)) return null
  const route = segments.join('/')
  const matchesByRealm: PortalApiRealm[] = []
  if (matches(method, route, customerExactRoutes, customerDynamicRoutes)) matchesByRealm.push('customer')
  if (matches(method, route, administratorExactRoutes, administratorDynamicRoutes)) {
    matchesByRealm.push('administrator')
  }
  if (matches(method, route, publicExactRoutes, publicDynamicRoutes)) matchesByRealm.push('public')
  return matchesByRealm.length === 1 ? matchesByRealm[0] : null
}

export function isAllowedPortalApiRoute(method: string, segments: string[]) {
  return getPortalApiRouteRealm(method, segments) !== null
}
