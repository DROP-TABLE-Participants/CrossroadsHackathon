import {
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from '@microsoft/signalr'
import { useEffect } from 'react'

type AdminOrchestrationRealtimeMessage = {
  projectId?: string
}

export function useAdminOrchestrationRealtime({
  enabled,
  onChanged,
  onSessionExpired,
}: {
  enabled: boolean
  onChanged: (projectId?: string) => Promise<unknown>
  onSessionExpired: () => void
}) {
  useEffect(() => {
    if (!enabled) return

    let cancelled = false
    let connectedBefore = false
    let reconnectAttempt = 0
    let reconnectTimer: number | undefined
    let refreshTimer: number | undefined
    const connection = new HubConnectionBuilder()
      .withUrl(new URL('/realtime/hubs/admin', window.location.href).toString(), {
        transport: HttpTransportType.WebSockets,
        skipNegotiation: true,
        withCredentials: true,
      })
      .configureLogging(LogLevel.Warning)
      .build()

    const refresh = (message?: AdminOrchestrationRealtimeMessage) => {
      if (refreshTimer) window.clearTimeout(refreshTimer)
      refreshTimer = window.setTimeout(() => {
        refreshTimer = undefined
        void onChanged(message?.projectId)
      }, 200)
    }
    connection.on('orchestrationChanged', refresh)

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
        const session = await fetch('/api/admin-auth/me', {
          method: 'GET',
          cache: 'no-store',
        })
        if (session.status === 401) {
          onSessionExpired()
          return
        }
        if (!session.ok) throw new Error('Administrator session could not be refreshed.')

        await connection.start()
        reconnectAttempt = 0
        if (connectedBefore) await onChanged()
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
      if (refreshTimer) window.clearTimeout(refreshTimer)
      connection.off('orchestrationChanged', refresh)
      void connection.stop()
    }
  }, [enabled, onChanged, onSessionExpired])
}
