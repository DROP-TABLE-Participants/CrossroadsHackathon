import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

import { getAdminLabels } from '@/components/admin/labels'
import { normalizeAdmin } from '@/components/admin/AdminSessionProvider'
import { formatOrchestrationCode } from '@/components/admin/formatOrchestrationCode'

const source = (relativePath: string) =>
  readFileSync(resolve(process.cwd(), relativePath), 'utf8')

describe('administrator experience', () => {
  it('keeps English and Bulgarian administration navigation aligned', () => {
    const en = getAdminLabels('en')
    const bg = getAdminLabels('bg')

    for (const key of ['overview', 'customers', 'projects', 'orchestration', 'administrators', 'demoBookings', 'auditLog'] as const) {
      expect(en[key]).toBeTruthy()
      expect(bg[key]).toBeTruthy()
      expect(bg[key]).not.toBe(en[key])
    }
  })

  it('uses a dedicated administrator shell and protected management routes', () => {
    const shell = source('src/components/admin/AdminSidebarShell.tsx')
    const protectedLayout = source('src/app/(frontend)/[locale]/admin/(protected)/layout.tsx')

    expect(shell).toContain('`${base}/customers`')
    expect(shell).toContain('`${base}/projects`')
    expect(shell).toContain('`${base}/orchestration`')
    expect(shell).toContain('`${base}/administrators`')
    expect(shell).toContain('`${base}/demo-bookings`')
    expect(shell).toContain('`${base}/audit`')
    expect(shell).not.toContain('/account')
    expect(protectedLayout).toContain('hasAdminSession')
    expect(protectedLayout).toContain('AdminGate')
    expect(protectedLayout).toContain('AdminSidebarShell')
  })

  it('provides realtime orchestration monitoring and guarded run recovery', () => {
    const overview = source('src/components/admin/AdminOrchestrationPage.tsx')
    const details = source('src/components/admin/AdminGenerationRunDetailsPage.tsx')
    const realtime = source('src/components/admin/hooks/useAdminOrchestrationRealtime.ts')
    const projectDetails = source('src/components/admin/AdminProjectDetailsPage.tsx')

    expect(overview).toContain('/api/admin/orchestration/overview')
    expect(overview).toContain('/api/admin/orchestration/runs?')
    expect(details).toContain('${action}')
    expect(details).toContain("control('cancel'")
    expect(details).toContain("control('resume'")
    expect(details).toContain('AdminRunControlDialog')
    expect(details).toContain('run.canRetryNow')
    expect(details).toContain('run.canCancel')
    expect(details).toContain('run.canResume')
    expect(details).not.toContain('resultJson')
    expect(realtime).toContain("'/realtime/hubs/admin'")
    expect(realtime).toContain('HttpTransportType.WebSockets')
    expect(realtime).toContain("connection.on('orchestrationChanged'")
    expect(realtime).not.toContain('setInterval')
    expect(projectDetails).toContain('/admin/orchestration/runs/')
  })

  it('localizes orchestration states and stage names in Bulgarian', () => {
    expect(formatOrchestrationCode('completed', 'bg')).toBe('Завършен')
    expect(formatOrchestrationCode('cancelled', 'bg')).toBe('Отказано')
    expect(formatOrchestrationCode('company_understanding', 'bg')).toBe('Разбиране на компанията')
    expect(formatOrchestrationCode('erp_authoring_attempt_2', 'bg')).toBe('Създаване на ERP · опит 2')
  })

  it('accepts only a complete administrator session shape', () => {
    expect(normalizeAdmin({
      id: 'admin-1',
      email: 'admin@example.test',
      name: 'Administrator',
      role: 'PlatformAdministrator',
    })).toEqual({
      id: 'admin-1',
      email: 'admin@example.test',
      name: 'Administrator',
      role: 'PlatformAdministrator',
    })
    expect(normalizeAdmin({ id: 'customer-1', email: 'customer@example.test', name: 'Customer' })).toBeNull()
  })
})
