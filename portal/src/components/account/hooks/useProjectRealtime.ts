import { useEffect } from 'react'
import {
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from '@microsoft/signalr'

type ProjectRealtimeMessage = {
  projectId?: string
}

type UseProjectRealtimeParams = {
  enabled: boolean
  polling?: boolean
  onProjectChanged: (projectId: string) => Promise<unknown>
  onProjectRemoved: () => Promise<unknown>
  onSessionExpired: () => void
}

export function useProjectRealtime({
  enabled,
  polling = false,
  onProjectChanged,
  onProjectRemoved,
  onSessionExpired,
}: UseProjectRealtimeParams) {
  useEffect(() => {
    if (!enabled) return

    // The local guest presentation runs without a WebSocket reverse proxy.
    if (polling) {
      let refreshing = false
      const timer = window.setInterval(async () => {
        if (refreshing || document.visibilityState === 'hidden') return
        refreshing = true
        try { await onProjectRemoved() } finally { refreshing = false }
      }, 5000)
      return () => window.clearInterval(timer)
    }

    let cancelled = false
    let connectedBefore = false
    let reconnectAttempt = 0
    let reconnectTimer: number | undefined
    const refreshTimers = new Map<string, number>()
    const connection = new HubConnectionBuilder()
      .withUrl(new URL('/realtime/hubs/customer', window.location.href).toString(), {
        transport: HttpTransportType.WebSockets,
        skipNegotiation: true,
        withCredentials: true,
      })
      .configureLogging(LogLevel.Warning)
      .build()

    const debounceProjectRefresh = (message: ProjectRealtimeMessage) => {
      const projectId = message?.projectId
      if (!projectId) return
      const current = refreshTimers.get(projectId)
      if (current) window.clearTimeout(current)
      refreshTimers.set(projectId, window.setTimeout(() => {
        refreshTimers.delete(projectId)
        void onProjectChanged(projectId)
      }, 200))
    }

    const refreshProjectList = () => {
      void onProjectRemoved()
    }

    connection.on('projectChanged', debounceProjectRefresh)
    connection.on('projectRemoved', refreshProjectList)

    const scheduleReconnect = () => {
      if (cancelled || reconnectTimer) return
      const delay = Math.min(30_000, 1_000 * (2 ** Math.min(reconnectAttempt, 5)))
      reconnectAttempt += 1
      reconnectTimer = window.setTimeout(() => {
        reconnectTimer = undefined
        void connect()
      }, delay)
    }

    const connect = async () => {
      if (cancelled) return
      try {
        // This BFF call rotates an expired HttpOnly access cookie before the
        // browser opens a socket. JavaScript never receives either token.
        const session = await fetch('/api/customers/me', {
          method: 'GET',
          cache: 'no-store',
        })
        if (session.status === 401) {
          onSessionExpired()
          return
        }
        if (!session.ok) throw new Error('Customer session could not be refreshed.')

        await connection.start()
        reconnectAttempt = 0
        if (connectedBefore) await onProjectRemoved()
        connectedBefore = true
      } catch {
        scheduleReconnect()
      }
    }

    connection.onclose(() => scheduleReconnect())
    void connect()

    return () => {
      cancelled = true
      if (reconnectTimer) window.clearTimeout(reconnectTimer)
      for (const timer of refreshTimers.values()) window.clearTimeout(timer)
      refreshTimers.clear()
      connection.off('projectChanged', debounceProjectRefresh)
      connection.off('projectRemoved', refreshProjectList)
      void connection.stop()
    }
  }, [enabled, polling, onProjectChanged, onProjectRemoved, onSessionExpired])
}
