import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import React from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DemoBookingProvider, useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { CUSTOMER_SESSION_HINT_KEY, CUSTOMER_USER_KEY, clearAuthSession } from '@/components/booking/customer-session'
import { installTestLocalStorage } from './helpers/local-storage'

vi.mock('next/navigation', () => ({ usePathname: () => '/en/account', useRouter: () => ({ push: vi.fn() }) }))
function SessionProbe() {
  const { customer, sessionResolved, sessionError, refreshCustomer } = useDemoBooking()
  return React.createElement('div', null,
    React.createElement('span', { 'data-testid': 'state' }, customer?.id || (sessionResolved && sessionError ? 'retry' : 'loading')),
    React.createElement('button', { onClick: () => void refreshCustomer() }, 'Retry'))
}
function renderProbe() { installTestLocalStorage(); render(React.createElement(DemoBookingProvider, null, React.createElement(SessionProbe))) }
afterEach(() => { cleanup(); clearAuthSession(); vi.unstubAllGlobals(); vi.clearAllMocks() })

describe('guest bootstrap', () => {
  it('resolves the server-owned session without storing user credentials or identity', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ user: { id: 'guest-1', email: 'guest-1@demo.invalid', isGuest: true } })))
    renderProbe()
    expect(await screen.findByText('guest-1')).toBeTruthy()
    expect(fetch).toHaveBeenCalledWith('/api/customers/guest-session', expect.objectContaining({ method: 'POST' }))
    expect(window.localStorage.getItem(CUSTOMER_SESSION_HINT_KEY)).toBeNull()
    expect(window.localStorage.getItem(CUSTOMER_USER_KEY)).toBeNull()
  })
  it('exposes retry after a temporary failure and recovers on the same bootstrap path', async () => {
    const upstream = vi.fn().mockRejectedValueOnce(new Error('Temporary failure')).mockResolvedValueOnce(Response.json({ user: { id: 'guest-1', email: 'guest-1@demo.invalid' } }))
    vi.stubGlobal('fetch', upstream)
    renderProbe()
    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('retry'))
    fireEvent.click(screen.getByText('Retry'))
    expect(await screen.findByText('guest-1')).toBeTruthy()
    expect(upstream).toHaveBeenCalledTimes(2)
  })
})
