'use client'

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'

export type AdminSessionUser = {
  id: string
  email: string
  name: string
  role: string
}

type AdminSessionContextValue = {
  admin: AdminSessionUser | null
  sessionResolved: boolean
  acceptSession: (admin: AdminSessionUser) => void
  logout: () => Promise<void>
}

const AdminSessionContext = createContext<AdminSessionContextValue | null>(null)

export function normalizeAdmin(value: unknown): AdminSessionUser | null {
  if (!value || typeof value !== 'object') return null
  const candidate = value as Partial<AdminSessionUser>
  return typeof candidate.id === 'string' &&
    typeof candidate.email === 'string' &&
    typeof candidate.name === 'string' &&
    typeof candidate.role === 'string'
    ? {
        id: candidate.id,
        email: candidate.email,
        name: candidate.name,
        role: candidate.role,
      }
    : null
}

export function AdminSessionProvider({ children }: { children: ReactNode }) {
  const [admin, setAdmin] = useState<AdminSessionUser | null>(null)
  const [sessionResolved, setSessionResolved] = useState(false)

  useEffect(() => {
    let active = true
    void fetch('/api/admin-auth/me', { cache: 'no-store' })
      .then(async (response) => (response.ok ? normalizeAdmin(await response.json()) : null))
      .catch(() => null)
      .then((current) => {
        if (!active) return
        setAdmin(current)
        setSessionResolved(true)
      })
    return () => {
      active = false
    }
  }, [])

  const acceptSession = useCallback((current: AdminSessionUser) => {
    setAdmin(current)
    setSessionResolved(true)
  }, [])

  const logout = useCallback(async () => {
    try {
      await fetch('/api/admin-auth/logout', { method: 'POST' })
    } finally {
      setAdmin(null)
      setSessionResolved(true)
    }
  }, [])

  const value = useMemo(
    () => ({ admin, sessionResolved, acceptSession, logout }),
    [admin, sessionResolved, acceptSession, logout],
  )

  return <AdminSessionContext.Provider value={value}>{children}</AdminSessionContext.Provider>
}

export function useAdminSession() {
  const value = useContext(AdminSessionContext)
  if (!value) throw new Error('useAdminSession must be used inside AdminSessionProvider.')
  return value
}
