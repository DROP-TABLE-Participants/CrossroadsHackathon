export type CustomerSession = {
  id: string
  email: string
  name?: string
  phoneNumber?: string
  companyName?: string
  representativeName?: string
  position?: string
  role?: string
  isGuest?: boolean
}

export type ActiveDemoBooking = {
  id: string
  email?: string
  phoneNumber?: string
  representativeName?: string
  position?: string
  companyName?: string
  startDate?: string
  endDate?: string
  status?: string
}

export const CUSTOMER_SESSION_HINT_KEY = 'customerSessionActive'
export const CUSTOMER_USER_KEY = 'customerAuthUser'
let sessionKnown = false

export function hasCustomerSessionHint() {
  if (typeof window === 'undefined') return null
  return sessionKnown
}

export function saveAuthSession(user: CustomerSession) {
  if (typeof window === 'undefined') return
  void user
  sessionKnown = true
  window.localStorage.removeItem(CUSTOMER_SESSION_HINT_KEY)
  window.localStorage.removeItem(CUSTOMER_USER_KEY)
}

export function clearAuthSession() {
  if (typeof window === 'undefined') return
  sessionKnown = false
  window.localStorage.removeItem(CUSTOMER_SESSION_HINT_KEY)
  window.localStorage.removeItem(CUSTOMER_USER_KEY)
}

export function shouldClearAuthSessionForCustomerProfileStatus(status: number) {
  return status === 401
}

export function normalizeCustomer(raw: unknown): CustomerSession | null {
  if (!raw || typeof raw !== 'object') return null

  const user = raw as Record<string, unknown>
  if (!user.id || !user.email) return null

  return {
    id: String(user.id),
    email: String(user.email),
    name: user.name ? String(user.name) : undefined,
    phoneNumber: user.phoneNumber ? String(user.phoneNumber) : undefined,
    companyName: user.companyName ? String(user.companyName) : undefined,
    representativeName: user.representativeName ? String(user.representativeName) : undefined,
    position: user.position ? String(user.position) : undefined,
    role: user.role ? String(user.role) : undefined,
    isGuest: user.isGuest === true,
  }
}

export function normalizeBooking(raw: unknown): ActiveDemoBooking | null {
  if (!raw || typeof raw !== 'object') return null

  const booking = raw as Record<string, unknown>
  if (!booking.id) return null

  return {
    id: String(booking.id),
    email: booking.email ? String(booking.email) : undefined,
    phoneNumber: booking.phoneNumber ? String(booking.phoneNumber) : undefined,
    representativeName: booking.representativeName ? String(booking.representativeName) : undefined,
    position: booking.position ? String(booking.position) : undefined,
    companyName: booking.companyName ? String(booking.companyName) : undefined,
    startDate: booking.startDate ? String(booking.startDate) : undefined,
    endDate: booking.endDate ? String(booking.endDate) : undefined,
    status: booking.status ? String(booking.status) : undefined,
  }
}
